namespace VmTyper.Models;

public sealed class TypingProgress
{
    public int TotalCharacters { get; init; }
    public int TypedCharacters { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double CharactersPerSecond { get; init; }

    public double PercentComplete => TotalCharacters == 0
        ? 0
        : Math.Clamp((double)TypedCharacters / TotalCharacters * 100.0, 0, 100);
}
