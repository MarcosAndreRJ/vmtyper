using System.Text.Json;
using VmTyper.Models;
using VmTyper.Services;

namespace VmTyper.Tests.Fakes;

/// <summary>Store em memória: guarda um instantâneo (cópia JSON) a cada Save.</summary>
internal sealed class InMemoryQueueStore : IQueueStore
{
    private string? _snapshot;

    public int SaveCount { get; private set; }

    public InMemoryQueueStore(IEnumerable<TextBlockItem>? initial = null)
    {
        if (initial is not null) _snapshot = JsonSerializer.Serialize(initial.ToList());
    }

    public List<TextBlockItem> Load() =>
        _snapshot is null ? new List<TextBlockItem>() : JsonSerializer.Deserialize<List<TextBlockItem>>(_snapshot)!;

    public void Save(IEnumerable<TextBlockItem> blocks)
    {
        SaveCount++;
        _snapshot = JsonSerializer.Serialize(blocks.ToList());
    }
}

/// <summary>Autosave disparado manualmente pelo teste.</summary>
internal sealed class ManualAutoSaveScheduler : IAutoSaveScheduler
{
    public event Action? Elapsed;

    public bool IsPending { get; private set; }

    public void Restart() => IsPending = true;

    public void Stop() => IsPending = false;

    public void Trigger()
    {
        IsPending = false;
        Elapsed?.Invoke();
    }
}

/// <summary>IProgress síncrono (Progress&lt;T&gt; posta no SynchronizationContext e chegaria tarde).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly object _lock = new();
    private readonly List<T> _reports = new();

    public IReadOnlyList<T> Reports
    {
        get { lock (_lock) return _reports.ToList(); }
    }

    public T Last
    {
        get { lock (_lock) return _reports[^1]; }
    }

    public void Report(T value)
    {
        lock (_lock) _reports.Add(value);
    }
}
