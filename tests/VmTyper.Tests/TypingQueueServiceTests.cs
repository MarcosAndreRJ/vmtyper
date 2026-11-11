using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class TypingQueueServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly InMemoryQueueStore _store = new();
    private readonly ManualAutoSaveScheduler _autoSave = new();

    public void Dispose() => _dir.Dispose();

    private TypingQueueService CreateQueue(params string[] texts)
    {
        var queue = new TypingQueueService(TestLog.Create(_dir), _store, _autoSave);
        for (int i = 1; i < texts.Length; i++) queue.AddBlock();
        for (int i = 0; i < texts.Length; i++) queue.Blocks[i].Text = texts[i];
        return queue;
    }

    // ------------------------------------------------------------------
    // Comportamento básico (linha de base + modelo v2)
    // ------------------------------------------------------------------

    [Fact]
    public void NewQueue_StartsWithOneEmptyBlock_AndNoCurrent()
    {
        var queue = new TypingQueueService(TestLog.Create(_dir), _store, _autoSave);

        Assert.Single(queue.Blocks);
        Assert.True(queue.Blocks[0].IsEmpty);
        Assert.Equal(TextBlockStatus.Empty, queue.Blocks[0].Status);
        Assert.Null(queue.Current);
        Assert.Null(queue.NextBlockId);
    }

    [Fact]
    public void FirstNonEmptyBlock_IsCurrent_SkippingEmptyOnes()
    {
        var queue = CreateQueue("", "b", "c");

        Assert.Same(queue.Blocks[1], queue.Current);
        Assert.True(queue.Blocks[1].IsNext);
        Assert.False(queue.Blocks[0].IsNext);
        Assert.False(queue.Blocks[2].IsNext);
    }

    [Fact]
    public void CompleteBlock_AdvancesToFollowing()
    {
        var queue = CreateQueue("a", "b");
        var first = queue.Current!;

        queue.BeginTyping(first.Id);
        queue.CompleteBlock(first.Id, clearTextAfterCompletion: false);

        Assert.Equal(TextBlockStatus.Sent, first.Status);
        Assert.Same(queue.Blocks[1], queue.Current);
    }

    [Fact]
    public void CancelBlock_KeepsSameBlockAsNext()
    {
        var queue = CreateQueue("a", "b");
        var first = queue.Current!;

        queue.BeginTyping(first.Id);
        queue.CancelBlock(first.Id, sentUpToIndex: 0);

        Assert.Equal(TextBlockStatus.Ready, first.Status);
        Assert.Same(first, queue.Current);
    }

    [Fact]
    public void EditingText_RestartsAutoSave()
    {
        var queue = CreateQueue("a");
        _autoSave.Stop();

        queue.Blocks[0].Text = "ab";

        Assert.True(_autoSave.IsPending);
    }

    // ------------------------------------------------------------------
    // Defeitos corrigidos na F1 §1 (A, B, D, N9)
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Known", "A")]
    public void EditingEmptyBlock_MakesItNext()
    {
        var queue = new TypingQueueService(TestLog.Create(_dir), _store, _autoSave);
        var block = queue.Blocks[0];

        block.Text = "conteúdo novo";

        Assert.Equal(TextBlockStatus.Ready, block.Status);
        Assert.Same(block, queue.Current);
        Assert.True(block.IsNext);
    }

    [Fact]
    [Trait("Known", "A")]
    public void ClearingCurrentBlock_MovesNextToFollowing()
    {
        var queue = CreateQueue("a", "b");
        Assert.Same(queue.Blocks[0], queue.Current);

        queue.Blocks[0].Text = string.Empty;

        Assert.Equal(TextBlockStatus.Empty, queue.Blocks[0].Status);
        Assert.Same(queue.Blocks[1], queue.Current);
    }

    [Fact]
    [Trait("Known", "B")]
    public void SetAsNext_WithEarlierReadyBlock_SelectsTarget()
    {
        var queue = CreateQueue("a", "b", "c");
        var target = queue.Blocks[2];

        Assert.True(queue.SetAsNext(target.Id));

        Assert.Same(target, queue.Current);
        Assert.Equal(target.Id, queue.ManualNextId);
        Assert.Equal(target.Id, queue.NextBlockId);
        Assert.True(target.IsNext);
        Assert.False(queue.Blocks[0].IsNext);
    }

    [Fact]
    public void SetAsNext_OnSentBlock_TransitionsToReadyWithoutAlteringOthers()
    {
        // §1.3 B: blocos [Ready, Ready, Sent] e SetAsNext(3) → próximo = 3 e os demais não mudam de status.
        var queue = CreateQueue("a", "b", "c");
        var b3 = queue.Blocks[2];
        queue.SetAsNext(b3.Id);
        queue.BeginTyping(b3.Id);
        queue.CompleteBlock(b3.Id, clearTextAfterCompletion: false);

        Assert.Equal(TextBlockStatus.Ready, queue.Blocks[0].Status);
        Assert.Equal(TextBlockStatus.Ready, queue.Blocks[1].Status);
        Assert.Equal(TextBlockStatus.Sent, b3.Status);

        Assert.True(queue.SetAsNext(b3.Id));
        Assert.Equal(TextBlockStatus.Ready, queue.Blocks[0].Status);
        Assert.Equal(TextBlockStatus.Ready, queue.Blocks[1].Status);
        Assert.Equal(TextBlockStatus.Ready, b3.Status);
        Assert.Same(b3, queue.Current);
    }

    [Fact]
    [Trait("Known", "D")]
    public void NewQueue_WhileTyping_IsRejected()
    {
        var queue = CreateQueue("a", "b");
        var typing = queue.Current!;
        queue.BeginTyping(typing.Id);

        Assert.False(queue.NewQueue());

        Assert.Contains(typing, queue.Blocks);
        Assert.Equal(TextBlockStatus.Typing, typing.Status);
        Assert.Equal(2, queue.Blocks.Count);
    }

    [Fact]
    [Trait("Known", "D")]
    public void RestartQueue_WhileTyping_IsRejected()
    {
        var queue = CreateQueue("a", "b");
        var first = queue.Current!;
        queue.BeginTyping(first.Id);
        queue.CompleteBlock(first.Id, clearTextAfterCompletion: false);
        var second = queue.Current!;
        queue.BeginTyping(second.Id);

        Assert.False(queue.RestartQueue());

        Assert.Equal(TextBlockStatus.Sent, first.Status);
        Assert.Equal(TextBlockStatus.Typing, second.Status);
    }

    [Fact]
    [Trait("Known", "D")]
    public void AddBlock_WhileTyping_IsRejected()
    {
        var queue = CreateQueue("a");
        queue.BeginTyping(queue.Current!.Id);

        Assert.Null(queue.AddBlock());

        Assert.Single(queue.Blocks);
    }

    [Fact]
    public void LockedQueue_RejectsAllMutators()
    {
        var queue = CreateQueue("a", "b");
        queue.IsLocked = true;

        Assert.False(queue.AddBlock(out _));
        Assert.False(queue.RemoveBlock(queue.Blocks[0].Id));
        Assert.False(queue.MoveDown(queue.Blocks[0].Id));
        Assert.False(queue.MoveUp(queue.Blocks[1].Id));
        Assert.False(queue.SetAsNext(queue.Blocks[1].Id));
        Assert.False(queue.RestartQueue());
        Assert.False(queue.NewQueue());
    }

    // ------------------------------------------------------------------
    // Tabela 1.2 completa: todas as células (De × Evento)
    // ------------------------------------------------------------------

    [Fact]
    public void StateMachine_Empty_Transitions()
    {
        var queue = CreateQueue("");
        var b = queue.Blocks[0];
        Assert.Equal(TextBlockStatus.Empty, b.Status);

        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.BeginTyping));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.PauseOrResume));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.AllSent));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.Cancel));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.Fail));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.SetAsNext));
        Assert.True(queue.Transition(b.Id, BlockTransitionEvent.Restart));
        Assert.Equal(TextBlockStatus.Empty, b.Status);

        b.Text = "abc";
        Assert.Equal(TextBlockStatus.Ready, b.Status);
    }

    [Fact]
    public void StateMachine_Ready_Transitions()
    {
        var queue = CreateQueue("abc", "def");
        var b = queue.Blocks[1];
        Assert.Equal(TextBlockStatus.Ready, b.Status);

        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.PauseOrResume));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.AllSent));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.Cancel));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.Fail));

        Assert.True(queue.Transition(b.Id, BlockTransitionEvent.SetAsNext));
        Assert.Equal(b.Id, queue.ManualNextId);

        Assert.True(queue.Transition(b.Id, BlockTransitionEvent.Restart));
        Assert.Equal(TextBlockStatus.Ready, b.Status);

        b.Text = "   ";
        Assert.Equal(TextBlockStatus.Empty, b.Status);

        b.Text = "def";
        Assert.True(queue.Transition(b.Id, BlockTransitionEvent.BeginTyping));
        Assert.Equal(TextBlockStatus.Typing, b.Status);
    }

    [Fact]
    public void StateMachine_TypingAndPaused_Transitions()
    {
        var queue = CreateQueue("abc", "def");
        var b = queue.Blocks[0];
        queue.BeginTyping(b.Id);
        Assert.Equal(TextBlockStatus.Typing, b.Status);

        // TextEdited bloqueado em Typing: mantém texto original.
        b.Text = "alterado durante digitação";
        Assert.Equal("abc", b.Text);

        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.BeginTyping));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.SetAsNext));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.Restart));

        // Pause / Resume
        Assert.True(queue.Transition(b.Id, BlockTransitionEvent.PauseOrResume));
        Assert.Equal(TextBlockStatus.Paused, b.Status);

        // TextEdited bloqueado também em Paused.
        b.Text = "alterado durante pausa";
        Assert.Equal("abc", b.Text);
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.AllSent));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.BeginTyping));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.SetAsNext));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.Restart));

        // Resume de volta para Typing
        Assert.True(queue.Transition(b.Id, BlockTransitionEvent.PauseOrResume));
        Assert.Equal(TextBlockStatus.Typing, b.Status);

        // Cancel com 0 unidades -> volta a Ready e continua sendo o próximo.
        Assert.True(queue.CancelBlock(b.Id, sentUpToIndex: 0));
        Assert.Equal(TextBlockStatus.Ready, b.Status);
        Assert.Same(b, queue.Current);

        // Cancel com >= 1 unidade -> PartiallySent e continua sendo o próximo (sem avanço indevido).
        queue.BeginTyping(b.Id);
        Assert.True(queue.CancelBlock(b.Id, sentUpToIndex: 2));
        Assert.Equal(TextBlockStatus.PartiallySent, b.Status);
        Assert.Equal(2, b.SentUpToIndex);
        Assert.Same(b, queue.Current);

        // Fail em Paused com 0 unidades -> Failed.
        b.SentUpToIndex = 0;
        queue.BeginTyping(b.Id);
        queue.PauseOrResumeBlock(b.Id);
        Assert.True(queue.FailBlock(b.Id, "erro simulado", sentUpToIndex: 0));
        Assert.Equal(TextBlockStatus.Failed, b.Status);
        Assert.Equal("erro simulado", b.LastError);
        Assert.Same(b, queue.Current);

        // Fail com >= 1 unidade -> PartiallySent.
        queue.BeginTyping(b.Id);
        Assert.True(queue.FailBlock(b.Id, "erro parcial", sentUpToIndex: 1));
        Assert.Equal(TextBlockStatus.PartiallySent, b.Status);
        Assert.Equal("erro parcial", b.LastError);
        Assert.Same(b, queue.Current);

        // AllSent -> Sent e avança para o bloco 2.
        queue.BeginTyping(b.Id);
        Assert.True(queue.CompleteBlock(b.Id, clearTextAfterCompletion: false));
        Assert.Equal(TextBlockStatus.Sent, b.Status);
        Assert.Same(queue.Blocks[1], queue.Current);
    }

    [Fact]
    public void StateMachine_Sent_And_PartialInterruptedFailed_Transitions()
    {
        var queue = CreateQueue("abc");
        var b = queue.Blocks[0];
        queue.BeginTyping(b.Id);
        queue.CompleteBlock(b.Id, clearTextAfterCompletion: false);
        Assert.Equal(TextBlockStatus.Sent, b.Status);

        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.BeginTyping));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.PauseOrResume));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.AllSent));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.Cancel));
        Assert.False(queue.Transition(b.Id, BlockTransitionEvent.Fail));

        // Editar bloco Sent dispara aviso SentBlockEdited e devolve para Ready.
        TextBlockItem? notified = null;
        queue.SentBlockEdited += item => notified = item;
        b.Text = "abc editado";
        Assert.Equal(TextBlockStatus.Ready, b.Status);
        Assert.Same(b, notified);

        // PartiallySent / Interrupted / Failed -> TextEdited zera índice e volta a Ready.
        queue.BeginTyping(b.Id);
        queue.CancelBlock(b.Id, sentUpToIndex: 3);
        Assert.Equal(TextBlockStatus.PartiallySent, b.Status);
        Assert.Equal(3, b.SentUpToIndex);

        b.Text = "novo texto";
        Assert.Equal(TextBlockStatus.Ready, b.Status);
        Assert.Equal(0, b.SentUpToIndex);
    }

    [Fact]
    public void V1Migration_And_InterruptedOnLoad()
    {
        string v1Json = """
        [
          { "Id": "11111111-1111-1111-1111-111111111111", "Order": 1, "Text": "bloco pronto", "Status": "Pending" },
          { "Id": "22222222-2222-2222-2222-222222222222", "Order": 2, "Text": "   ", "Status": "Pending" },
          { "Id": "33333333-3333-3333-3333-333333333333", "Order": 3, "Text": "bloco atual", "Status": "Current" },
          { "Id": "44444444-4444-4444-4444-444444444444", "Order": 4, "Text": "bloco concluido", "Status": "Completed" },
          { "Id": "55555555-5555-5555-5555-555555555555", "Order": 5, "Text": "em envio quando fechou", "Status": "Typing" },
          { "Id": "66666666-6666-6666-6666-666666666666", "Order": 6, "Text": "pausado quando fechou", "Status": "Paused" }
        ]
        """;

        var path = _dir.File("queue.json");
        System.IO.File.WriteAllText(path, v1Json);

        var queue = new TypingQueueService(TestLog.Create(_dir), new QueuePersistenceService(path), _autoSave);

        Assert.Equal(TextBlockStatus.Ready, queue.Blocks[0].Status);
        Assert.Equal(TextBlockStatus.Empty, queue.Blocks[1].Status);
        Assert.Equal(TextBlockStatus.Ready, queue.Blocks[2].Status);
        Assert.Equal(TextBlockStatus.Sent, queue.Blocks[3].Status);
        Assert.Equal(TextBlockStatus.Interrupted, queue.Blocks[4].Status);
        Assert.Equal(TextBlockStatus.Interrupted, queue.Blocks[5].Status);
        Assert.Same(queue.Blocks[0], queue.Current);
    }
}
