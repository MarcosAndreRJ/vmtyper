using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using VmTyper.Models;

namespace VmTyper.Services;

/// <summary>
/// Único ponto responsável pelas regras de navegação e transição da fila de
/// blocos de texto (modelo v2).
///
/// Regra central v2 (ADR 0002):
///  - "Próximo" não é mais um status: fica em <see cref="NextBlockId"/>,
///    calculado a partir de <see cref="ManualNextId"/> (se apontar para um
///    bloco existente em Ready/PartiallySent/Interrupted/Failed) ou do
///    primeiro bloco em <see cref="TextBlockStatus.Ready"/> na ordem.
///  - Todas as transições de estado passam pelo método único
///    <see cref="Transition"/>.
/// </summary>
public sealed class TypingQueueService
{
    private readonly IQueueStore _persistence;
    private readonly LogService _log;
    private readonly IAutoSaveScheduler _autoSaveTimer;

    private bool _suppressPropertyCallback;
    private readonly Dictionary<Guid, string> _lockedTextSnapshot = new();

    public ObservableCollection<TextBlockItem> Blocks { get; } = new();

    private bool _isLocked;
    /// <summary>Quando verdadeiro (sessão ativa no coordenador), bloqueia qualquer mutação da fila.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked == value) return;
            _isLocked = value;
            _lockedTextSnapshot.Clear();
            if (value)
            {
                foreach (var b in Blocks) _lockedTextSnapshot[b.Id] = b.Text;
            }
        }
    }

    public Guid? ManualNextId { get; private set; }
    public Guid? NextBlockId { get; private set; }

    /// <summary>Disparado após qualquer mutação relevante da fila (para a UI atualizar rótulos/resumos).</summary>
    public event Action? Changed;

    /// <summary>Disparado quando um bloco já enviado (Sent) é editado e devolvido para Ready.</summary>
    public event Action<TextBlockItem>? SentBlockEdited;

    /// <summary>Disparado quando uma gravação em disco falha (§4.1).</summary>
    public event Action<string>? PersistenceFailed
    {
        add => _persistence.PersistenceFailed += value;
        remove => _persistence.PersistenceFailed -= value;
    }

    public Persistence.LoadResult<List<TextBlockItem>>? LastLoadResult => _persistence.LastLoadResult;

    public TypingQueueService(LogService log, IQueueStore persistence, IAutoSaveScheduler autoSave)
    {
        _log = log;
        _persistence = persistence;
        _autoSaveTimer = autoSave;
        _autoSaveTimer.Elapsed += Save;

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
        if (_suppressPropertyCallback || sender is not TextBlockItem block) return;

        if (e.PropertyName == nameof(TextBlockItem.Text))
        {
            if (block.Status is TextBlockStatus.Typing or TextBlockStatus.Paused)
            {
                // Edição bloqueada durante Typing/Paused: reverte ao texto salvo.
                if (_lockedTextSnapshot.TryGetValue(block.Id, out var original) && block.Text != original)
                {
                    _log.Warn($"Edição bloqueada no Bloco {block.Order} (estado {block.Status}).");
                    _suppressPropertyCallback = true;
                    try { block.Text = original; }
                    finally { _suppressPropertyCallback = false; }
                }
                return;
            }

            _lockedTextSnapshot[block.Id] = block.Text;
            Transition(block.Id, BlockTransitionEvent.TextEdited);
            _autoSaveTimer.Restart();
        }
    }

    // ------------------------------------------------------------------
    // Consultas
    // ------------------------------------------------------------------

    /// <summary>Bloco apontado por <see cref="NextBlockId"/>, ou null se não houver nenhum elegível.</summary>
    public TextBlockItem? Current => NextBlockId.HasValue ? Find(NextBlockId.Value) : null;

    public int TotalBlocks => Blocks.Count;
    public int CompletedCount => Blocks.Count(b => b.Status == TextBlockStatus.Sent);
    public int PendingCount => TotalBlocks - CompletedCount;

    /// <summary>Retorna o bloco a digitar no próximo acionamento, ou null se não houver nenhum elegível.</summary>
    public TextBlockItem? PeekNext() => Current;

    // ------------------------------------------------------------------
    // Máquina de estados (tabela 1.2)
    // ------------------------------------------------------------------

    /// <summary>
    /// Método único de transição da máquina de estados dos blocos (§1.2).
    /// Retorna <c>true</c> se a transição foi válida e aplicada, ou <c>false</c> se proibida (✗).
    /// </summary>
    public bool Transition(
        Guid id,
        BlockTransitionEvent evt,
        int? sentUpToIndex = null,
        string? error = null,
        Guid? sessionId = null,
        bool clearTextOnSent = false)
    {
        var block = Find(id);
        if (block is null) return false;

        if (sentUpToIndex.HasValue) block.SentUpToIndex = sentUpToIndex.Value;
        if (sessionId.HasValue) block.LastSessionId = sessionId.Value;

        var from = block.Status;
        bool wasSentEdited = false;

        switch (from)
        {
            case TextBlockStatus.Empty:
                switch (evt)
                {
                    case BlockTransitionEvent.TextEdited:
                        if (!block.IsEmpty) block.Status = TextBlockStatus.Ready;
                        break;
                    case BlockTransitionEvent.Restart:
                        block.Status = TextBlockStatus.Empty;
                        block.SentUpToIndex = 0;
                        block.LastError = null;
                        break;
                    default:
                        return false;
                }
                break;

            case TextBlockStatus.Ready:
                switch (evt)
                {
                    case BlockTransitionEvent.TextEdited:
                        if (block.IsEmpty) block.Status = TextBlockStatus.Empty;
                        break;
                    case BlockTransitionEvent.BeginTyping:
                        if (block.IsEmpty) return false;
                        _lockedTextSnapshot[block.Id] = block.Text;
                        block.Status = TextBlockStatus.Typing;
                        block.LastError = null;
                        break;
                    case BlockTransitionEvent.SetAsNext:
                        ManualNextId = block.Id;
                        break;
                    case BlockTransitionEvent.Restart:
                        block.Status = block.IsEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready;
                        block.SentUpToIndex = 0;
                        block.LastError = null;
                        break;
                    default:
                        return false;
                }
                break;

            case TextBlockStatus.Typing:
                switch (evt)
                {
                    case BlockTransitionEvent.PauseOrResume:
                        block.Status = TextBlockStatus.Paused;
                        break;
                    case BlockTransitionEvent.AllSent:
                        block.Status = TextBlockStatus.Sent;
                        block.SentUpToIndex = block.Text.Length;
                        block.LastError = null;
                        if (ManualNextId == block.Id) ManualNextId = null;
                        if (clearTextOnSent)
                        {
                            _suppressPropertyCallback = true;
                            try { block.Text = string.Empty; }
                            finally { _suppressPropertyCallback = false; }
                        }
                        break;
                    case BlockTransitionEvent.Cancel:
                        if (block.SentUpToIndex > 0)
                        {
                            block.Status = TextBlockStatus.PartiallySent;
                            ManualNextId = block.Id;
                        }
                        else
                        {
                            block.Status = TextBlockStatus.Cancelled;
                            block.Status = block.IsEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready;
                        }
                        break;
                    case BlockTransitionEvent.Fail:
                        block.LastError = error;
                        if (block.SentUpToIndex > 0)
                        {
                            block.Status = TextBlockStatus.PartiallySent;
                        }
                        else
                        {
                            block.Status = TextBlockStatus.Failed;
                        }
                        ManualNextId = block.Id;
                        break;
                    default:
                        return false;
                }
                break;

            case TextBlockStatus.Paused:
                switch (evt)
                {
                    case BlockTransitionEvent.PauseOrResume:
                        block.Status = TextBlockStatus.Typing;
                        break;
                    case BlockTransitionEvent.Cancel:
                        if (block.SentUpToIndex > 0)
                        {
                            block.Status = TextBlockStatus.PartiallySent;
                            ManualNextId = block.Id;
                        }
                        else
                        {
                            block.Status = TextBlockStatus.Cancelled;
                            block.Status = block.IsEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready;
                        }
                        break;
                    case BlockTransitionEvent.Fail:
                        block.LastError = error;
                        if (block.SentUpToIndex > 0)
                        {
                            block.Status = TextBlockStatus.PartiallySent;
                        }
                        else
                        {
                            block.Status = TextBlockStatus.Failed;
                        }
                        ManualNextId = block.Id;
                        break;
                    default:
                        return false;
                }
                break;

            case TextBlockStatus.Sent:
                switch (evt)
                {
                    case BlockTransitionEvent.TextEdited:
                        block.SentUpToIndex = 0;
                        block.LastError = null;
                        block.Status = block.IsEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready;
                        wasSentEdited = !block.IsEmpty;
                        break;
                    case BlockTransitionEvent.SetAsNext:
                        if (block.IsEmpty) return false;
                        block.SentUpToIndex = 0;
                        block.LastError = null;
                        block.Status = TextBlockStatus.Ready;
                        ManualNextId = block.Id;
                        break;
                    case BlockTransitionEvent.Restart:
                        block.SentUpToIndex = 0;
                        block.LastError = null;
                        block.Status = block.IsEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready;
                        break;
                    default:
                        return false;
                }
                break;

            case TextBlockStatus.PartiallySent:
            case TextBlockStatus.Interrupted:
            case TextBlockStatus.Failed:
                switch (evt)
                {
                    case BlockTransitionEvent.TextEdited:
                        block.SentUpToIndex = 0;
                        block.LastError = null;
                        block.Status = block.IsEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready;
                        break;
                    case BlockTransitionEvent.BeginTyping:
                        if (block.IsEmpty) return false;
                        _lockedTextSnapshot[block.Id] = block.Text;
                        block.Status = TextBlockStatus.Typing;
                        block.LastError = null;
                        break;
                    case BlockTransitionEvent.SetAsNext:
                        if (block.IsEmpty) return false;
                        ManualNextId = block.Id;
                        break;
                    case BlockTransitionEvent.Restart:
                        block.SentUpToIndex = 0;
                        block.LastError = null;
                        block.Status = block.IsEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready;
                        break;
                    default:
                        return false;
                }
                break;

            default:
                return false;
        }

        RecalculateNext();
        if (wasSentEdited)
        {
            SentBlockEdited?.Invoke(block);
        }
        return true;
    }

    // ------------------------------------------------------------------
    // Atalhos de transição (chamados pelo MainViewModel e testes)
    // ------------------------------------------------------------------

    public bool BeginTyping(Guid id, Guid? sessionId = null)
        => Transition(id, BlockTransitionEvent.BeginTyping, sessionId: sessionId);

    public bool PauseOrResumeBlock(Guid id)
        => Transition(id, BlockTransitionEvent.PauseOrResume);

    public bool CompleteBlock(Guid id, bool clearTextAfterCompletion, int? sentUnits = null, Guid? sessionId = null)
    {
        bool ok = Transition(id, BlockTransitionEvent.AllSent, sentUpToIndex: sentUnits, sessionId: sessionId, clearTextOnSent: clearTextAfterCompletion);
        if (ok) Save();
        return ok;
    }

    public bool CancelBlock(Guid id, int sentUpToIndex = 0, Guid? sessionId = null)
    {
        bool ok = Transition(id, BlockTransitionEvent.Cancel, sentUpToIndex: sentUpToIndex, sessionId: sessionId);
        if (ok) Save();
        return ok;
    }

    public bool FailBlock(Guid id, string error, int sentUpToIndex = 0, Guid? sessionId = null)
    {
        bool ok = Transition(id, BlockTransitionEvent.Fail, sentUpToIndex: sentUpToIndex, error: error, sessionId: sessionId);
        if (ok) Save();
        return ok;
    }

    // ------------------------------------------------------------------
    // Edição da fila (todos os mutadores verificam IsLocked / bloco ativo)
    // ------------------------------------------------------------------

    private bool EnsureEditable(string operation)
    {
        if (IsLocked || Blocks.Any(b => b.Status is TextBlockStatus.Typing or TextBlockStatus.Paused))
        {
            _log.Warn($"Operação '{operation}' recusada: a fila está bloqueada durante uma sessão ativa.");
            return false;
        }
        return true;
    }

    public bool AddBlock(out TextBlockItem? added)
    {
        if (!EnsureEditable(nameof(AddBlock)))
        {
            added = null;
            return false;
        }

        var block = new TextBlockItem
        {
            Order = Blocks.Count + 1,
            Text = string.Empty,
            Status = TextBlockStatus.Empty
        };
        Blocks.Add(block);
        RenumberAndRecalculate();
        Save();
        added = block;
        return true;
    }

    public TextBlockItem? AddBlock()
    {
        AddBlock(out var block);
        return block;
    }

    public bool RemoveBlock(Guid id)
    {
        if (!EnsureEditable(nameof(RemoveBlock))) return false;

        var block = Find(id);
        if (block is null || block.Status is TextBlockStatus.Typing or TextBlockStatus.Paused) return false;

        if (ManualNextId == id) ManualNextId = null;
        Blocks.Remove(block);
        if (Blocks.Count == 0)
        {
            Blocks.Add(new TextBlockItem { Order = 1, Text = string.Empty, Status = TextBlockStatus.Empty });
        }

        RenumberAndRecalculate();
        Save();
        return true;
    }

    public bool MoveUp(Guid id)
    {
        if (!EnsureEditable(nameof(MoveUp))) return false;

        int index = IndexOf(id);
        if (index <= 0 || Blocks[index].Status is TextBlockStatus.Typing or TextBlockStatus.Paused) return false;

        Blocks.Move(index, index - 1);
        RenumberAndRecalculate();
        Save();
        return true;
    }

    public bool MoveDown(Guid id)
    {
        if (!EnsureEditable(nameof(MoveDown))) return false;

        int index = IndexOf(id);
        if (index < 0 || index >= Blocks.Count - 1 || Blocks[index].Status is TextBlockStatus.Typing or TextBlockStatus.Paused) return false;

        Blocks.Move(index, index + 1);
        RenumberAndRecalculate();
        Save();
        return true;
    }

    /// <summary>
    /// Define manualmente um bloco como o próximo a digitar, gravando
    /// <see cref="ManualNextId"/> sem alterar o status de outros blocos (§1.3 B).
    /// </summary>
    public bool SetAsNext(Guid id)
    {
        if (!EnsureEditable(nameof(SetAsNext))) return false;

        bool ok = Transition(id, BlockTransitionEvent.SetAsNext);
        if (ok) Save();
        return ok;
    }

    public bool RestartQueue()
    {
        if (!EnsureEditable(nameof(RestartQueue))) return false;

        ManualNextId = null;
        foreach (var block in Blocks)
        {
            Transition(block.Id, BlockTransitionEvent.Restart);
        }

        RecalculateNext();
        Save();
        return true;
    }

    public bool NewQueue()
    {
        if (!EnsureEditable(nameof(NewQueue))) return false;

        ManualNextId = null;
        Blocks.Clear();
        Blocks.Add(new TextBlockItem { Order = 1, Text = string.Empty, Status = TextBlockStatus.Empty });
        RenumberAndRecalculate();
        Save();
        return true;
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

        _suppressPropertyCallback = true;
        try
        {
            Blocks.Clear();
            foreach (var block in loaded.OrderBy(b => b.Order))
            {
                NormalizeLoadedBlock(block);
                Blocks.Add(block);
            }

            if (Blocks.Count == 0)
            {
                Blocks.Add(new TextBlockItem { Order = 1, Text = string.Empty, Status = TextBlockStatus.Empty });
            }
        }
        finally
        {
            _suppressPropertyCallback = false;
        }

        RenumberAndRecalculate();
        _log.Info($"Fila carregada: {Blocks.Count} bloco(s), {CompletedCount} enviado(s).");
    }

    /// <summary>
    /// Aplica as regras de carregamento (§1.1):
    /// Typing/Paused ao carregar viram Interrupted; Empty/Ready coerentes com o texto.
    /// </summary>
    public static void NormalizeLoadedBlock(TextBlockItem block)
    {
        if (block.Status is TextBlockStatus.Typing or TextBlockStatus.Paused)
        {
            block.Status = TextBlockStatus.Interrupted;
        }
        else if (block.Status == TextBlockStatus.Cancelled)
        {
            block.Status = block.IsEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready;
        }
        else if (block.Status == TextBlockStatus.Ready && block.IsEmpty)
        {
            block.Status = TextBlockStatus.Empty;
        }
        else if (block.Status == TextBlockStatus.Empty && !block.IsEmpty)
        {
            block.Status = TextBlockStatus.Ready;
        }
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
        RecalculateNext();
    }

    private static bool IsManualNextEligible(TextBlockItem block) =>
        !block.IsEmpty && block.Status is
            TextBlockStatus.Ready or
            TextBlockStatus.PartiallySent or
            TextBlockStatus.Interrupted or
            TextBlockStatus.Failed;

    /// <summary>
    /// Calcula <see cref="NextBlockId"/> e atualiza <see cref="TextBlockItem.IsNext"/>:
    /// 1. Se houver um bloco em Typing/Paused, nenhum outro é "próximo" ativo, ou mantém o ponteiro calculado.
    /// 2. Se <see cref="ManualNextId"/> aponta para um bloco elegível (Ready/PartiallySent/Interrupted/Failed), ele é o próximo.
    /// 3. Caso contrário, é o primeiro bloco em <see cref="TextBlockStatus.Ready"/> na ordem.
    /// </summary>
    private void RecalculateNext()
    {
        Guid? resolved = null;

        if (ManualNextId.HasValue)
        {
            var manual = Find(ManualNextId.Value);
            if (manual is not null && IsManualNextEligible(manual))
            {
                resolved = manual.Id;
            }
            else if (manual is null || manual.Status is not (TextBlockStatus.Typing or TextBlockStatus.Paused))
            {
                ManualNextId = null;
            }
        }

        if (resolved is null)
        {
            var firstReady = Blocks.FirstOrDefault(b => b.Status == TextBlockStatus.Ready && !b.IsEmpty);
            resolved = firstReady?.Id;
        }

        NextBlockId = resolved;
        foreach (var block in Blocks)
        {
            block.IsNext = resolved.HasValue && block.Id == resolved.Value;
        }

        Changed?.Invoke();
    }
}
