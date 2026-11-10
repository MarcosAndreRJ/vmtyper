using System.Diagnostics;

namespace VmTyper.Services;

public readonly record struct DelaySample(TimeSpan Requested, TimeSpan Actual);

/// <summary>
/// Abstração de temporização precisa para hold (KeyDown→KeyUp, não cancelável)
/// e espera entre unidades (cancelável) (Fase 2 §1.1).
/// </summary>
public interface IDelayProvider
{
    /// <summary>Atraso curto e não cancelável entre KeyDown e KeyUp.</summary>
    void HoldExact(TimeSpan d);

    /// <summary>Atraso cancelável entre o fim de uma unidade e o início da próxima.</summary>
    ValueTask WaitAsync(TimeSpan d, CancellationToken ct);

    /// <summary>Carimbo de alta resolução (<see cref="Stopwatch.GetTimestamp"/>).</summary>
    long Timestamp();
}

