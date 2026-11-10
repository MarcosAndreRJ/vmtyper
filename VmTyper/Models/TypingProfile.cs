namespace VmTyper.Models;

public enum ProfileKind
{
    Legacy,
    Fast,
    Compatibility,
    Natural,
    Custom
}

/// <summary>Intervalo inclusivo de milissegundos [Min, Max] serializável em JSON.</summary>
public readonly record struct MsRange(int Min, int Max)
{
    public static MsRange Fixed(int ms) => new(ms, ms);
}

/// <summary>
/// Pisos e tetos absolutos de segurança para Hold e Gap (Fase 2 §2).
/// MaxHoldMs padrão (60 ms) fica abaixo do repeat delay mínimo do Windows (250 ms).
/// </summary>
public sealed record SafetyLimits(
    int MinHoldMs = 1,
    int MaxHoldMs = 60,
    int MinGapMs = 0,
    int MaxGapMs = 2000);

/// <summary>
/// Perfil de temporização de digitação (Fase 2 §2).
/// Os valores iniciais são hipóteses registradas no ADR 0007 para calibração na F3.
/// </summary>
public sealed record TypingProfile
{
    public string Name { get; init; } = "Legacy";
    public ProfileKind Kind { get; init; } = ProfileKind.Legacy;
    public MsRange HoldMs { get; init; } = MsRange.Fixed(2);
    public MsRange GapMs { get; init; } = MsRange.Fixed(2);
    public MsRange WordPauseMs { get; init; } = MsRange.Fixed(0);
    public double WordPauseChance { get; init; }
    public MsRange PunctuationPauseMs { get; init; } = MsRange.Fixed(0);
    public MsRange EnterPauseMs { get; init; } = MsRange.Fixed(20);
    public double SpeedVariationPct { get; init; }
    public double Correlation { get; init; }
    public int SameKeyMinGapMs { get; init; }
    public double CodeLineMultiplier { get; init; } = 1.0;
    public SafetyLimits Limits { get; init; } = new();
    public int? Seed { get; init; }

    public static TypingProfile Legacy(int charDelayMs = 2, int enterDelayMs = 20, int? seed = null) => new()
    {
        Name = "Legacy",
        Kind = ProfileKind.Legacy,
        HoldMs = MsRange.Fixed(2),
        GapMs = MsRange.Fixed(Math.Clamp(charDelayMs, 0, 2000)),
        WordPauseMs = MsRange.Fixed(0),
        WordPauseChance = 0.0,
        PunctuationPauseMs = MsRange.Fixed(0),
        EnterPauseMs = MsRange.Fixed(Math.Clamp(enterDelayMs, 0, 2000)),
        SpeedVariationPct = 0.0,
        Correlation = 0.0,
        SameKeyMinGapMs = 0,
        CodeLineMultiplier = 1.0,
        Limits = new SafetyLimits(MinHoldMs: 1, MaxHoldMs: 60, MinGapMs: 0, MaxGapMs: 2000),
        Seed = seed
    };

    public static TypingProfile Fast(int? seed = null) => new()
    {
        Name = "Fast",
        Kind = ProfileKind.Fast,
        HoldMs = new MsRange(2, 4),
        GapMs = new MsRange(3, 8),
        WordPauseMs = MsRange.Fixed(0),
        WordPauseChance = 0.0,
        PunctuationPauseMs = MsRange.Fixed(0),
        EnterPauseMs = MsRange.Fixed(20),
        SpeedVariationPct = 0.08,
        Correlation = 0.35,
        SameKeyMinGapMs = 6,
        CodeLineMultiplier = 1.10,
        Limits = new SafetyLimits(MinHoldMs: 1, MaxHoldMs: 60, MinGapMs: 0, MaxGapMs: 2000),
        Seed = seed
    };

    public static TypingProfile Compatibility(int? seed = null) => new()
    {
        Name = "Compatibility",
        Kind = ProfileKind.Compatibility,
        HoldMs = new MsRange(8, 15),
        GapMs = new MsRange(25, 40),
        WordPauseMs = MsRange.Fixed(0),
        WordPauseChance = 0.0,
        PunctuationPauseMs = MsRange.Fixed(40),
        EnterPauseMs = MsRange.Fixed(80),
        SpeedVariationPct = 0.08,
        Correlation = 0.35,
        SameKeyMinGapMs = 30,
        CodeLineMultiplier = 1.15,
        Limits = new SafetyLimits(MinHoldMs: 1, MaxHoldMs: 60, MinGapMs: 0, MaxGapMs: 2000),
        Seed = seed
    };

    public static TypingProfile Natural(int? seed = null) => new()
    {
        Name = "Natural",
        Kind = ProfileKind.Natural,
        HoldMs = new MsRange(30, 60),
        GapMs = new MsRange(60, 180),
        WordPauseMs = new MsRange(80, 200),
        WordPauseChance = 0.25,
        PunctuationPauseMs = new MsRange(150, 300),
        EnterPauseMs = new MsRange(300, 600),
        SpeedVariationPct = 0.22,
        Correlation = 0.75,
        SameKeyMinGapMs = 75,
        CodeLineMultiplier = 1.20,
        Limits = new SafetyLimits(MinHoldMs: 1, MaxHoldMs: 60, MinGapMs: 0, MaxGapMs: 2000),
        Seed = seed
    };

    public static TypingProfile Resolve(AppSettings settings)
    {
        if (!settings.Features.AdaptiveTiming)
        {
            return Legacy(settings.CharDelayMs, settings.EnterDelayMs);
        }

        return (settings.TypingProfile?.Trim()) switch
        {
            "Fast" => Fast(),
            "Compatibility" => Compatibility(),
            "Natural" => Natural(),
            "Custom" when settings.CustomProfile is not null => settings.CustomProfile with { Kind = ProfileKind.Custom, Name = "Custom" },
            _ => Legacy(settings.CharDelayMs, settings.EnterDelayMs)
        };
    }
}

