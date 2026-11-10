using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using VmTyper.ViewModels;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class AutomaticNextBlockRecognitionTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly InMemoryQueueStore _store = new();
    private readonly ManualAutoSaveScheduler _autoSave = new();
    private readonly LogService _log;

    public AutomaticNextBlockRecognitionTests()
    {
        _log = TestLog.Create(_dir);
    }

    public void Dispose() => _dir.Dispose();

    private (TypingQueueService Queue, MainViewModel Vm) CreateHarness(params string[] texts)
    {
        var initialBlocks = texts.Length > 0
            ? texts.Select((t, i) => new TextBlockItem
            {
                Order = i + 1,
                Text = t,
                Status = string.IsNullOrWhiteSpace(t) ? TextBlockStatus.Empty : TextBlockStatus.Ready
            }).ToList()
            : null;

        var store = new InMemoryQueueStore(initialBlocks);
        var queue = new TypingQueueService(_log, store, _autoSave);
        var settingsSvc = new SettingsService(_dir.File("settings.json"));
        settingsSvc.Save(new AppSettings { CountdownSeconds = 0, CharDelayMs = 0, EnterDelayMs = 0 });

        var hotkeys = new FakeHotkeyRegistrar();
        using var engineHarness = new EngineHarness();
        var coordinator = new TypingSessionCoordinator(hotkeys, _log);

        var vm = new MainViewModel(
            engineHarness.Engine,
            new ActiveWindowService(),
            settingsSvc,
            new HistoryService(_dir.File("history.json")),
            hotkeys,
            queue,
            _log,
            coordinator,
            (_, _) => Task.CompletedTask);

        return (queue, vm);
    }

    [Fact]
    public void EmptyBlock_WhenReceivesText_AutomaticallyBecomesNextAndEnablesDigitarButton()
    {
        // 1. Inicia com um bloco vazio
        var (queue, vm) = CreateHarness("");

        Assert.Single(vm.Blocks);
        Assert.Equal(TextBlockStatus.Empty, vm.Blocks[0].Status);
        Assert.False(vm.Blocks[0].IsNext);
        Assert.Equal("○ Vazio", vm.Blocks[0].StatusLabel);
        Assert.False(vm.HasNextBlock);
        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal("DIGITAR", vm.DigitarButtonText);

        bool canExecuteFired = false;
        vm.StartCommand.CanExecuteChanged += (_, _) => canExecuteFired = true;

        // 2. Usuário insere texto no bloco
        vm.Blocks[0].Text = "Texto válido digitado pelo usuário";

        // 3. Deve assumir imediatamente "Próximo" e habilitar o botão
        Assert.Equal(TextBlockStatus.Ready, vm.Blocks[0].Status);
        Assert.True(vm.Blocks[0].IsNext);
        Assert.Equal("▶ Próximo ▶", vm.Blocks[0].StatusLabel);
        Assert.True(vm.HasNextBlock);
        Assert.True(vm.CanStart);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.True(canExecuteFired);
        Assert.Equal("DIGITAR BLOCO 1", vm.DigitarButtonText);
        Assert.Equal("Próximo: Bloco 1 de 1", vm.NextBlockLabel);
        Assert.Same(vm.Blocks[0], queue.Current);
    }

    [Fact]
    public void Block_WhenLosesAllContent_RecalculatesEligibilityAndDisablesDigitarButton()
    {
        var (queue, vm) = CreateHarness("Texto inicial");
        Assert.True(vm.HasNextBlock);
        Assert.True(vm.CanStart);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal("▶ Próximo ▶", vm.Blocks[0].StatusLabel);

        bool canExecuteFired = false;
        vm.StartCommand.CanExecuteChanged += (_, _) => canExecuteFired = true;

        // Apaga todo o conteúdo
        vm.Blocks[0].Text = string.Empty;

        Assert.Equal(TextBlockStatus.Empty, vm.Blocks[0].Status);
        Assert.False(vm.Blocks[0].IsNext);
        Assert.Equal("○ Vazio", vm.Blocks[0].StatusLabel);
        Assert.False(vm.HasNextBlock);
        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(canExecuteFired);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void Block_WithOnlyWhitespaceOrNewlines_RemainsEmptyAndNotEligible()
    {
        var (queue, vm) = CreateHarness("");

        // Inserção contendo apenas espaços, tabulações e quebras de linha
        vm.Blocks[0].Text = "   \t\n   \r\n   ";

        Assert.True(vm.Blocks[0].IsEmpty);
        Assert.Equal(TextBlockStatus.Empty, vm.Blocks[0].Status);
        Assert.False(vm.Blocks[0].IsNext);
        Assert.Equal("○ Vazio", vm.Blocks[0].StatusLabel);
        Assert.False(vm.HasNextBlock);
        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Null(queue.Current);
    }

    [Fact]
    public void CancelledBlock_RemainsEligibleForRetry()
    {
        var (queue, vm) = CreateHarness("Bloco para cancelar");
        var b = vm.Blocks[0];

        // Inicia digitação e cancela sem unidades enviadas
        queue.BeginTyping(b.Id);
        queue.CancelBlock(b.Id, sentUpToIndex: 0);

        // Deve continuar elegível como próximo bloco
        Assert.Same(b, queue.Current);
        Assert.True(b.IsNext);
        Assert.True(vm.HasNextBlock);
        Assert.True(vm.CanStart);
        Assert.True(vm.StartCommand.CanExecute(null));

        // Teste de bloco explicitamente marcado como Cancelled
        b.Status = TextBlockStatus.Cancelled;
        Assert.True(TypingQueueService.IsBlockEligible(b));
        queue.CancelBlock(b.Id, sentUpToIndex: 0);
        Assert.Same(b, queue.Current);
    }

    [Fact]
    public void CompletedBlock_WhenEditedWithNewContent_ReactivatesAsReadyWithoutAffectingOtherCompletedBlocks()
    {
        var (queue, vm) = CreateHarness("Bloco 1", "Bloco 2");

        // Conclui ambos os blocos
        queue.BeginTyping(vm.Blocks[0].Id);
        queue.CompleteBlock(vm.Blocks[0].Id, clearTextAfterCompletion: false);
        queue.BeginTyping(vm.Blocks[1].Id);
        queue.CompleteBlock(vm.Blocks[1].Id, clearTextAfterCompletion: false);

        Assert.Equal(TextBlockStatus.Sent, vm.Blocks[0].Status);
        Assert.Equal(TextBlockStatus.Sent, vm.Blocks[1].Status);
        Assert.False(vm.HasNextBlock);
        Assert.False(vm.CanStart);

        // Edita o bloco 1 com novo texto
        vm.Blocks[0].Text = "Bloco 1 reeditado";

        // Bloco 1 deve voltar a Ready e assumir Próximo; Bloco 2 permanece Sent
        Assert.Equal(TextBlockStatus.Ready, vm.Blocks[0].Status);
        Assert.True(vm.Blocks[0].IsNext);
        Assert.Equal("▶ Próximo ▶", vm.Blocks[0].StatusLabel);

        Assert.Equal(TextBlockStatus.Sent, vm.Blocks[1].Status);
        Assert.False(vm.Blocks[1].IsNext);
        Assert.Equal("↗ Enviado", vm.Blocks[1].StatusLabel);

        Assert.True(vm.HasNextBlock);
        Assert.True(vm.CanStart);
        Assert.Equal("DIGITAR BLOCO 1", vm.DigitarButtonText);
        Assert.Same(vm.Blocks[0], queue.Current);
    }

    [Fact]
    public void MultiplePendingBlocks_SelectsFirstEligibleByOrder()
    {
        // Fila com Bloco 1 vazio, Bloco 2 com texto, Bloco 3 com texto
        var (queue, vm) = CreateHarness("", "Texto Bloco 2", "Texto Bloco 3");

        // O primeiro elegível em ordem é o Bloco 2
        Assert.Equal(TextBlockStatus.Empty, vm.Blocks[0].Status);
        Assert.False(vm.Blocks[0].IsNext);

        Assert.Equal(TextBlockStatus.Ready, vm.Blocks[1].Status);
        Assert.True(vm.Blocks[1].IsNext);
        Assert.Equal("▶ Próximo ▶", vm.Blocks[1].StatusLabel);

        Assert.Equal(TextBlockStatus.Ready, vm.Blocks[2].Status);
        Assert.False(vm.Blocks[2].IsNext);
        Assert.Equal("● Pronto", vm.Blocks[2].StatusLabel);

        Assert.Same(vm.Blocks[1], queue.Current);

        // Agora o usuário digita texto no Bloco 1
        vm.Blocks[0].Text = "Agora Bloco 1 tem texto";

        // Como Bloco 1 vem primeiro na ordem, deve se tornar automaticamente o Próximo
        Assert.Equal(TextBlockStatus.Ready, vm.Blocks[0].Status);
        Assert.True(vm.Blocks[0].IsNext);
        Assert.Equal("▶ Próximo ▶", vm.Blocks[0].StatusLabel);

        Assert.Equal(TextBlockStatus.Ready, vm.Blocks[1].Status);
        Assert.False(vm.Blocks[1].IsNext);
        Assert.Equal("● Pronto", vm.Blocks[1].StatusLabel);

        Assert.Same(vm.Blocks[0], queue.Current);
        Assert.Equal("DIGITAR BLOCO 1", vm.DigitarButtonText);
    }

    [Fact]
    public void ManualSelection_IsRespectedOverDefaultOrder()
    {
        var (queue, vm) = CreateHarness("Bloco 1", "Bloco 2");

        // Seleção padrão é Bloco 1
        Assert.Same(vm.Blocks[0], queue.Current);

        // Usuário clica "Definir como próximo" no Bloco 2
        queue.SetAsNext(vm.Blocks[1].Id);
        Assert.Same(vm.Blocks[1], queue.Current);
        Assert.True(vm.Blocks[1].IsNext);
        Assert.False(vm.Blocks[0].IsNext);

        // Edição no Bloco 1 não deve sobrescrever a seleção manual válida de Bloco 2
        vm.Blocks[0].Text = "Bloco 1 alterado";
        Assert.Same(vm.Blocks[1], queue.Current);
        Assert.True(vm.Blocks[1].IsNext);

        // Se o Bloco 2 manualmente selecionado for esvaziado, a seleção manual cai e Bloco 1 assume
        vm.Blocks[1].Text = string.Empty;
        Assert.Same(vm.Blocks[0], queue.Current);
        Assert.True(vm.Blocks[0].IsNext);
        Assert.False(vm.Blocks[1].IsNext);
    }

    [Fact]
    public void PreservationOfCompletedBlocks_WhenOtherBlocksAreAddedOrEdited()
    {
        var (queue, vm) = CreateHarness("Bloco 1");

        // Conclui Bloco 1
        queue.BeginTyping(vm.Blocks[0].Id);
        queue.CompleteBlock(vm.Blocks[0].Id, clearTextAfterCompletion: false, sentUnits: 7);
        Assert.Equal(TextBlockStatus.Sent, vm.Blocks[0].Status);
        Assert.Equal(7, vm.Blocks[0].SentUpToIndex);

        // Adiciona Bloco 2 e Bloco 3 e edita o Bloco 3
        queue.AddBlock();
        queue.AddBlock();
        vm.Blocks[2].Text = "Conteúdo novo no Bloco 3";

        // Bloco 1 permanece intacto em Sent com seus metadados preservados
        Assert.Equal(TextBlockStatus.Sent, vm.Blocks[0].Status);
        Assert.Equal(7, vm.Blocks[0].SentUpToIndex);
        Assert.False(vm.Blocks[0].IsNext);

        // Bloco 3 é o único elegível e assume Próximo
        Assert.Equal(TextBlockStatus.Ready, vm.Blocks[2].Status);
        Assert.True(vm.Blocks[2].IsNext);
        Assert.Same(vm.Blocks[2], queue.Current);
    }

    [Fact]
    public void EditingPrevented_DuringTypingSession()
    {
        var (queue, vm) = CreateHarness("Bloco 1 original", "Bloco 2");
        var b = vm.Blocks[0];

        // Inicia digitação do Bloco 1
        queue.BeginTyping(b.Id);
        Assert.Equal(TextBlockStatus.Typing, b.Status);

        // Tentativa de edição durante digitação deve ser revertida
        b.Text = "Texto modificado durante digitação";
        Assert.Equal("Bloco 1 original", b.Text);

        // Trava de sessão ativa (IsLocked) também impede mutações estruturais
        queue.IsLocked = true;
        Assert.False(queue.AddBlock(out _));
        Assert.False(queue.RemoveBlock(vm.Blocks[1].Id));
        Assert.False(queue.MoveDown(b.Id));
        Assert.False(queue.RestartQueue());
        Assert.False(queue.NewQueue());
    }

    [Fact]
    public void ImmediateDigitarButtonUpdate_OnContentChangeWithoutDebounceDelay()
    {
        var (queue, vm) = CreateHarness("");

        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));

        bool buttonCanExecuteChanged = false;
        vm.StartCommand.CanExecuteChanged += (_, _) => buttonCanExecuteChanged = true;

        // O usuário insere texto: a notificação visual e a habilitação do botão ocorrem
        // de forma imediata e síncrona, antes de qualquer disparo do timer de persistência (1,5s)
        vm.Blocks[0].Text = "Texto imediato";

        Assert.True(buttonCanExecuteChanged);
        Assert.True(vm.CanStart);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal("▶ Próximo ▶", vm.Blocks[0].StatusLabel);
        Assert.Equal("DIGITAR BLOCO 1", vm.DigitarButtonText);
    }
}

