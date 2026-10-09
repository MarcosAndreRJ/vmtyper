using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using VmTyper.Models;

namespace VmTyper.Services;

/// <summary>
/// Único ponto responsável pelas regras de navegação da fila de blocos de
/// texto: qual é o próximo bloco, quando avançar, como reagir a
/// cancelamento/pausa, reordenação, remoção e reinício. Nenhuma dessas
/// regras deve viver espalhada em code-behind ou no ViewModel — tudo passa
/// por aqui, o que torna as transições de estado previsíveis e evita saltos
/// indevidos de bloco (ver seções 31-33 do pedido de evolução).
///
/// Regra central: a cada momento existe NO MÁXIMO um bloco com status
/// <see cref="TextBlockStatus.Current"/> (o próximo elegível, pulando
/// blocos vazios) e NO MÁXIMO um com <see cref="TextBlockStatus.Typing"/>
/// (o que está em execução agora). <see cref="RecalculateStatuses"/> é a
/// única função que decide qual bloco é "Current" — ela é determinística a
/// partir da ordem e do status "Completed"/vazio de cada bloco, então nunca
/// há necessidade de manipular um índice solto em vários lugares.
/// </summary>
public sealed class TypingQueueService
{
    private readonly QueuePersistenceService _persistence;
    private readonly LogService _log;
    private readonly DispatcherTimer _autoSaveTimer;

    public ObservableCollection<TextBlockItem> Blocks { get; } = new();

    /// <summary>Disparado após qualquer mutação relevante da fila (para a UI atualizar rótulos/resumos).</summary>
    public event Action? Changed;

    public TypingQueueService(LogService log, Dispatcher dispatcher, QueuePersistenceService persistence)
    {
        _log = log;
        _persistence = persistence;

        // Autosave "debounced": edições de texto reiniciam o timer, e só
        // gravamos em disco 1.5s depois da última digitação — evita I/O a
        // cada tecla pressionada, mas garante que o conteúdo preparado não
        // se perca se o aplicativo for fechado inesperadamente.
        _autoSaveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            Save();
        };

        Blocks.CollectionChanged += OnBlocksCollectionChanged;
        LoadInternal();
    }

    private void OnBlocksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (TextBlockItem item in e.NewItems)
                item.PropertyChanged += OnBlockPropertyChanged;

        if (e.OldItems is not null)
            foreach (TextBlockItem item in e.OldItems)
                item.PropertyChanged -= OnBlockPropertyChanged;
    }

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TextBlockItem.Text))
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }
    }

    // ------------------------------------------------------------------
    // Consultas
    // ------------------------------------------------------------------

    public TextBlockItem? Current => Blocks.FirstOrDefault(b => b.Status == TextBlockStatus.Current);

    public int TotalBlocks => Blocks.Count;
    public int CompletedCount => Blocks.Count(b => b.Status == TextBlockStatus.Completed);
    public int PendingCount => TotalBlocks - CompletedCount;

    /// <summary>Retorna o bloco a digitar no próximo acionamento, ou null se não houver nenhum elegível.</summary>
    public TextBlockItem? PeekNext() => Current;

    // ------------------------------------------------------------------
    // Transições de execução (chamadas pelo MainViewModel)
    // ------------------------------------------------------------------

    public void BeginTyping(Guid id)
    {
        var block = Find(id);
        if (block is null) return;
        block.Status = TextBlockStatus.Typing;
        Changed?.Invoke();
    }

    public void CompleteBlock(Guid id, bool clearTextAfterCompletion)
    {
        var block = Find(id);
        if (block is null) return;

        block.Status = TextBlockStatus.Completed;
        if (clearTextAfterCompletion)
        {
            // Mantém o bloco (identidade/posição/histórico), apenas esvazia
            // o conteúdo já digitado.
            block.Text = string.Empty;
        }

        RecalculateStatuses();
        Save();
    }

    /// <summary>
    /// Usado tanto para ESC/cancelamento quanto para erro inesperado: o
    /// bloco volta a ser o próximo a digitar (nunca avança nem é perdido).
    /// </summary>
    public void CancelBlock(Guid id)
    {
        var block = Find(id);
        if (block is null) return;

        block.Status = TextBlockStatus.Pending;
        RecalculateStatuses();
        Save();
    }

    // ------------------------------------------------------------------
    // Edição da fila
    // ------------------------------------------------------------------

    public TextBlockItem AddBlock()
    {
        var block = new TextBlockItem
        {
            Order = Blocks.Count + 1,
            Text = string.Empty,
            Status = TextBlockStatus.Pending
        };
        Blocks.Add(block);
        RenumberAndRecalculate();
        Save();
        return block;
    }

    public void RemoveBlock(Guid id)
    {
        var block = Find(id);
        if (block is null || block.Status == TextBlockStatus.Typing) return;

        Blocks.Remove(block);
        if (Blocks.Count == 0)
        {
            Blocks.Add(new TextBlockItem { Order = 1, Text = string.Empty, Status = TextBlockStatus.Pending });
        }

        RenumberAndRecalculate();
        Save();
    }

    public void MoveUp(Guid id)
    {
        int index = IndexOf(id);
        if (index <= 0 || Blocks[index].Status == TextBlockStatus.Typing) return;

        Blocks.Move(index, index - 1);
        RenumberAndRecalculate();
        Save();
    }

    public void MoveDown(Guid id)
    {
        int index = IndexOf(id);
        if (index < 0 || index >= Blocks.Count - 1 || Blocks[index].Status == TextBlockStatus.Typing) return;

        Blocks.Move(index, index + 1);
        RenumberAndRecalculate();
        Save();
    }

    /// <summary>
    /// Define manualmente um bloco como o próximo a digitar. Blocos a partir
    /// da posição do alvo (inclusive) que estavam "Concluído" ou "Próximo"
    /// voltam a "Pendente" antes de recalcular — blocos ANTES do alvo não
    /// são tocados, preservando o que já foi de fato digitado.
    /// </summary>
    public void SetAsNext(Guid id)
    {
        var target = Find(id);
        if (target is null || target.Status == TextBlockStatus.Typing) return;

        foreach (var block in Blocks.Where(b => b.Order >= target.Order))
        {
            if (block.Status is TextBlockStatus.Completed or TextBlockStatus.Current)
                block.Status = TextBlockStatus.Pending;
        }

        RecalculateStatuses();
        Save();
    }

    public void RestartQueue()
    {
        foreach (var block in Blocks)
        {
            if (block.Status != TextBlockStatus.Typing)
                block.Status = TextBlockStatus.Pending;
        }

        RecalculateStatuses();
        Save();
    }

    public void NewQueue()
    {
        Blocks.Clear();
        Blocks.Add(new TextBlockItem { Order = 1, Text = string.Empty, Status = TextBlockStatus.Pending });
        RecalculateStatuses();
        Save();
    }

    // ------------------------------------------------------------------
    // Persistência
    // ------------------------------------------------------------------

    public void Save()
    {
        _persistence.Save(Blocks);
    }

    private void LoadInternal()
    {
        var loaded = _persistence.Load();

        Blocks.Clear();
        foreach (var block in loaded.OrderBy(b => b.Order))
        {
            // Uma execução que estava em andamento quando o app foi fechado
            // (ou travou) não pode ser restaurada como "Digitando" — nunca
            // terminou de fato, então volta a ser candidata normal.
            if (block.Status == TextBlockStatus.Typing)
                block.Status = TextBlockStatus.Pending;

            Blocks.Add(block);
        }

        if (Blocks.Count == 0)
        {
            Blocks.Add(new TextBlockItem { Order = 1, Text = string.Empty, Status = TextBlockStatus.Pending });
        }

        RenumberAndRecalculate();
        _log.Info($"Fila carregada: {Blocks.Count} bloco(s), {CompletedCount} concluído(s).");
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private TextBlockItem? Find(Guid id) => Blocks.FirstOrDefault(b => b.Id == id);

    private int IndexOf(Guid id)
    {
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (Blocks[i].Id == id) return i;
        }
        return -1;
    }

    private void RenumberAndRecalculate()
    {
        int order = 1;
        foreach (var block in Blocks) block.Order = order++;
        RecalculateStatuses();
    }

    /// <summary>
    /// Única função que decide qual bloco é "Current". Percorre a fila em
    /// ordem: blocos "Completed" ficam intocados; um bloco "Typing" (em
    /// execução) também fica intocado e "consome" a vaga de bloco ativo;
    /// o primeiro bloco não-concluído e não-vazio encontrado depois disso
    /// vira "Current"; qualquer outro bloco que estivesse marcado "Current"
    /// indevidamente volta a "Pending". Blocos vazios nunca recebem
    /// "Current" — são pulados automaticamente (seção 12).
    /// </summary>
    private void RecalculateStatuses()
    {
        bool assigned = false;

        foreach (var block in Blocks)
        {
            if (block.Status == TextBlockStatus.Completed) continue;

            if (block.Status == TextBlockStatus.Typing)
            {
                assigned = true;
                continue;
            }

            if (!assigned && !block.IsEmpty)
            {
                block.Status = TextBlockStatus.Current;
                assigned = true;
            }
            else if (block.Status == TextBlockStatus.Current)
            {
                block.Status = TextBlockStatus.Pending;
            }
        }

        Changed?.Invoke();
    }
}
