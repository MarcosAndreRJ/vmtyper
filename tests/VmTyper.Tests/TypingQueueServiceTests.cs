using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;

namespace VmTyper.Tests;

public sealed class TypingQueueServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly InMemoryQueueStore _store = new();
    private readonly ManualAutoSaveScheduler _autoSave = new();

    public void Dispose() => _dir.Dispose();

    private TypingQueueService CreateQueue(params string[] texts)
    {
        var queue = new TypingQueueService(TestLog.Create(_dir), _store, _autoSave);
        // A fila começa com um bloco vazio; os demais são adicionados.
        for (int i = 1; i < texts.Length; i++) queue.AddBlock();
        for (int i = 0; i < texts.Length; i++) queue.Blocks[i].Text = texts[i];
        // Força o recálculo como a UI faz hoje (qualquer ação de fila recalcula).
        if (texts.Length > 0) queue.RestartQueue();
        return queue;
    }

    // ------------------------------------------------------------------
    // Comportamento atual correto (linha de base, verdes)
    // ------------------------------------------------------------------

    [Fact]
    public void NewQueue_StartsWithOneEmptyBlock_AndNoCurrent()
    {
        var queue = new TypingQueueService(TestLog.Create(_dir), _store, _autoSave);

        Assert.Single(queue.Blocks);
        Assert.True(queue.Blocks[0].IsEmpty);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void FirstNonEmptyBlock_IsCurrent_SkippingEmptyOnes()
    {
        var queue = CreateQueue("", "b", "c");

        Assert.Same(queue.Blocks[1], queue.Current);
    }

    [Fact]
    public void CompleteBlock_AdvancesToFollowing()
    {
        var queue = CreateQueue("a", "b");
        var first = queue.Current!;

        queue.BeginTyping(first.Id);
        queue.CompleteBlock(first.Id, clearTextAfterCompletion: false);

        Assert.Equal(TextBlockStatus.Completed, first.Status);
        Assert.Same(queue.Blocks[1], queue.Current);
    }

    [Fact]
    public void CancelBlock_KeepsSameBlockAsNext()
    {
        var queue = CreateQueue("a", "b");
        var first = queue.Current!;

        queue.BeginTyping(first.Id);
        queue.CancelBlock(first.Id);

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
    // Defeitos conhecidos (comportamento correto esperado)
    // ------------------------------------------------------------------

    [KnownDefectFact("A")]
    [Trait("Known", "A")]
    public void EditingEmptyBlock_MakesItNext()
    {
        var queue = new TypingQueueService(TestLog.Create(_dir), _store, _autoSave);
        var block = queue.Blocks[0];

        block.Text = "conteúdo novo";

        Assert.Same(block, queue.Current);
    }

    [KnownDefectFact("A")]
    [Trait("Known", "A")]
    public void ClearingCurrentBlock_MovesNextToFollowing()
    {
        var queue = CreateQueue("a", "b");
        Assert.Same(queue.Blocks[0], queue.Current);

        queue.Blocks[0].Text = string.Empty;

        Assert.Same(queue.Blocks[1], queue.Current);
    }

    [KnownDefectFact("B")]
    [Trait("Known", "B")]
    public void SetAsNext_WithEarlierReadyBlock_SelectsTarget()
    {
        var queue = CreateQueue("a", "b", "c");
        var target = queue.Blocks[2];

        queue.SetAsNext(target.Id);

        Assert.Same(target, queue.Current);
    }

    [KnownDefectFact("D")]
    [Trait("Known", "D")]
    public void NewQueue_WhileTyping_IsRejected()
    {
        var queue = CreateQueue("a", "b");
        var typing = queue.Current!;
        queue.BeginTyping(typing.Id);

        queue.NewQueue();

        Assert.Contains(typing, queue.Blocks);
        Assert.Equal(TextBlockStatus.Typing, typing.Status);
        Assert.Equal(2, queue.Blocks.Count);
    }

    [KnownDefectFact("D")]
    [Trait("Known", "D")]
    public void RestartQueue_WhileTyping_IsRejected()
    {
        var queue = CreateQueue("a", "b");
        var first = queue.Current!;
        queue.BeginTyping(first.Id);
        queue.CompleteBlock(first.Id, clearTextAfterCompletion: false);
        var second = queue.Current!;
        queue.BeginTyping(second.Id);

        queue.RestartQueue();

        Assert.Equal(TextBlockStatus.Completed, first.Status);
        Assert.Equal(TextBlockStatus.Typing, second.Status);
    }

    [KnownDefectFact("D")]
    [Trait("Known", "D")]
    public void AddBlock_WhileTyping_IsRejected()
    {
        var queue = CreateQueue("a");
        queue.BeginTyping(queue.Current!.Id);

        queue.AddBlock();

        Assert.Single(queue.Blocks);
    }
}
