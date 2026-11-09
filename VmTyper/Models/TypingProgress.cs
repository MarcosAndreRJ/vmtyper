namespace VmTyper.Models;

public sealed class TypingProgress
{
    public int TotalUnits { get; init; }
    public int Sent { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double CharactersPerSecond { get; init; }

    /// <summary>Compatibilidade com bindings e testes: aponta para <see cref="TotalUnits"/>.</summary>
    public int TotalCharacters
    {
        get => TotalUnits;
        init => TotalUnits = value;
    }

    /// <summary>Compatibilidade com bindings e testes: aponta para <see cref="Sent"/> (apenas unidades efetivamente enviadas).</summary>
    public int TypedCharacters
    {
        get => Sent;
        init => Sent = value;
    }

    public double PercentComplete => TotalUnits == 0
        ? 0
        : Math.Clamp((double)(Sent + Skipped + Failed) / TotalUnits * 100.0, 0, 100);
}
