using VmTyper.Models;

namespace VmTyper.Services;

public readonly record struct KeyTiming(TimeSpan Hold, TimeSpan GapAfter, string Reason);

public sealed record TimingContext(
    InputUnit Current,
    InputUnit? Previous,
    InputUnit? NextUnit,
    int PositionInWord,
    int WordLength,
    int PositionInLine,
    bool IsCodeLine);

/// <summary>
/// Política pura e determinística (dada uma semente) de temporização por unidade (Fase 2 §3).
/// Nunca altera o conteúdo nem os eventos do plano: calcula apenas <c>Hold</c> e <c>GapAfter</c>.
/// </summary>
public sealed class TypingTimingPolicy
{
    private static readonly HashSet<string> PunctuationGraphemes = new(StringComparer.Ordinal)
    {
        ".", ",", ";", ":", "!", "?"
    };

    private static readonly char[] CodeChars = { '{', '}', '(', ')', ';', '=' };

    private readonly TypingProfile _profile;
    private readonly Random _rng;
    private double _tempo = 1.0;

    public TypingProfile Profile => _profile;

    public TypingTimingPolicy(TypingProfile profile)
    {
        _profile = profile;
        _rng = profile.Seed.HasValue ? new Random(profile.Seed.Value) : new Random();
    }

    public KeyTiming Next(TimingContext ctx)
    {
        var reasons = new List<string>(4) { _profile.Kind.ToString() };
        var limits = _profile.Limits;

        // 1. Deriva de tempo correlacionada AR(1): tempo_t = φ·tempo_{t-1} + (1-φ)·1 + ε
        double phi = Math.Clamp(_profile.Correlation, 0.0, 0.95);
        double varPct = Math.Max(0.0, _profile.SpeedVariationPct);
        if (varPct > 0.0)
        {
            double sigma = varPct * Math.Sqrt(Math.Max(0.05, 1.0 - phi * phi));
            double eps = SampleTruncatedNormal(0.0, sigma, -2.5 * sigma, 2.5 * sigma);
            _tempo = phi * _tempo + (1.0 - phi) * 1.0 + eps;
            _tempo = Math.Clamp(_tempo, Math.Max(0.25, 1.0 - 2.0 * varPct), 1.0 + 2.0 * varPct);
        }
        else
        {
            _tempo = 1.0;
        }

        // 2 & 3. Gap base e pausas contextuais
        double gapMs;
        if (_profile.Kind == ProfileKind.Legacy)
        {
            if (ctx.Current.IsLineBreak)
            {
                gapMs = _profile.EnterPauseMs.Min;
                reasons.Add("Enter");
            }
            else
            {
                gapMs = _profile.GapMs.Min;
            }
        }
        else
        {
            double baseGap = SampleTruncatedLogNormal(_profile.GapMs) * _tempo;
            if (ctx.IsCodeLine && _profile.CodeLineMultiplier > 1.0)
            {
                baseGap *= _profile.CodeLineMultiplier;
                reasons.Add("CodeLine");
            }

            gapMs = baseGap;

            if (ctx.Current.IsLineBreak)
            {
                gapMs += SampleUniform(_profile.EnterPauseMs) * _tempo;
                reasons.Add("Enter");
            }
            else if (PunctuationGraphemes.Contains(ctx.Current.Text))
            {
                double punct = SampleUniform(_profile.PunctuationPauseMs);
                if (punct > 0)
                {
                    gapMs += punct * _tempo;
                    reasons.Add("Punctuation");
                }
            }
            else if (ctx.Current.Text == " " && _profile.WordPauseChance > 0 && _rng.NextDouble() < _profile.WordPauseChance)
            {
                double wordPause = SampleUniform(_profile.WordPauseMs);
                double lengthFactor = 1.0 + Math.Clamp((ctx.WordLength - 4) * 0.08, 0.0, 0.6);
                gapMs += wordPause * lengthFactor * _tempo;
                reasons.Add("WordEnd");
            }
        }

        // 4. Unidades idênticas consecutivas (piso SameKeyMinGapMs)
        if (_profile.SameKeyMinGapMs > 0 &&
            ctx.NextUnit is not null &&
            AreSameLogicalUnit(ctx.Current, ctx.NextUnit))
        {
            if (gapMs < _profile.SameKeyMinGapMs)
            {
                gapMs = _profile.SameKeyMinGapMs;
            }
            reasons.Add("SameKeyFloor");
        }

        // 5. Hold amostrado em HoldMs com correlação fraca com o tempo
        double holdBase = SampleUniform(_profile.HoldMs);
        double holdTempoFactor = 1.0 + 0.30 * (_tempo - 1.0);
        double holdMs = Math.Clamp(holdBase * holdTempoFactor, _profile.HoldMs.Min, _profile.HoldMs.Max);

        // 6. Clamp final em SafetyLimits
        int clampedHoldMs = Math.Clamp((int)Math.Round(holdMs), limits.MinHoldMs, limits.MaxHoldMs);
        int clampedGapMs = Math.Clamp((int)Math.Round(gapMs), limits.MinGapMs, limits.MaxGapMs);

        return new KeyTiming(
            TimeSpan.FromMilliseconds(clampedHoldMs),
            TimeSpan.FromMilliseconds(clampedGapMs),
            string.Join("+", reasons));
    }

    /// <summary>
    /// Gera toda a sequência de <see cref="KeyTiming"/> para uma lista de unidades planejadas,
    /// construindo o <see cref="TimingContext"/> de cada posição (palavra, linha, heurística de código).
    /// </summary>
    public IReadOnlyList<KeyTiming> GenerateAll(IReadOnlyList<InputUnit> units)
    {
        if (units.Count == 0) return Array.Empty<KeyTiming>();

        var contexts = BuildContexts(units);
        var result = new KeyTiming[units.Count];
        for (int i = 0; i < units.Count; i++)
        {
            result[i] = Next(contexts[i]);
        }
        return result;
    }

    public static IReadOnlyList<TimingContext> BuildContexts(IReadOnlyList<InputUnit> units)
    {
        if (units.Count == 0) return Array.Empty<TimingContext>();

        // Pré-calcula quais linhas são linhas de código e o tamanho das palavras.
        var isCodeLineByUnit = new bool[units.Count];
        var posInLineByUnit = new int[units.Count];
        int lineStart = 0;

        while (lineStart < units.Count)
        {
            int lineEnd = lineStart;
            int leadingSpaces = 0;
            bool inLeading = true;
            int codeSymbolCount = 0;

            while (lineEnd < units.Count && !units[lineEnd].IsLineBreak)
            {
                string g = units[lineEnd].Text;
                if (inLeading)
                {
                    if (g == " ") leadingSpaces++;
                    else if (g == "\t") leadingSpaces += 4;
                    else inLeading = false;
                }
                if (g.Length == 1 && CodeChars.Contains(g[0]))
                {
                    codeSymbolCount++;
                }
                lineEnd++;
            }

            bool isCode = leadingSpaces >= 2 || codeSymbolCount >= 2;
            int endInclusive = Math.Min(lineEnd, units.Count - 1);
            for (int idx = lineStart; idx <= endInclusive; idx++)
            {
                isCodeLineByUnit[idx] = isCode;
                posInLineByUnit[idx] = idx - lineStart;
            }

            lineStart = lineEnd + 1;
        }

        var contexts = new TimingContext[units.Count];
        int currentWordLen = 0;
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            int posInWord;
            int wordLen;

            if (u.Text == " " || u.IsLineBreak || u.Text == "\t")
            {
                posInWord = currentWordLen;
                wordLen = currentWordLen;
                currentWordLen = 0;
            }
            else if (PunctuationGraphemes.Contains(u.Text))
            {
                posInWord = currentWordLen;
                wordLen = currentWordLen;
            }
            else
            {
                posInWord = currentWordLen;
                currentWordLen++;
                wordLen = currentWordLen;
            }

            contexts[i] = new TimingContext(
                u,
                i > 0 ? units[i - 1] : null,
                i + 1 < units.Count ? units[i + 1] : null,
                posInWord,
                wordLen,
                posInLineByUnit[i],
                isCodeLineByUnit[i]);
        }

        return contexts;
    }

    private static bool AreSameLogicalUnit(InputUnit a, InputUnit b)
    {
        if (string.Equals(a.Text, b.Text, StringComparison.Ordinal))
        {
            return true;
        }

        if (a.Events.Count > 0 && b.Events.Count > 0)
        {
            var lastDownA = a.Events.LastOrDefault(e => e.Kind == KeyEventKind.Down);
            var firstDownB = b.Events.FirstOrDefault(e => e.Kind == KeyEventKind.Down);
            if (lastDownA.Code != 0 &&
                lastDownA.Code == firstDownB.Code &&
                lastDownA.Enc == firstDownB.Enc &&
                lastDownA.Extended == firstDownB.Extended)
            {
                return true;
            }
        }

        return false;
    }

    private double SampleUniform(MsRange range)
    {
        if (range.Max <= range.Min) return range.Min;
        return range.Min + _rng.NextDouble() * (range.Max - range.Min);
    }

    private double SampleTruncatedLogNormal(MsRange range)
    {
        if (range.Max <= range.Min) return range.Min;

        double min = Math.Max(0.5, range.Min);
        double max = Math.Max(min + 0.5, range.Max);
        // Mediana posicionada a 40% do intervalo para assimetria positiva realista.
        double median = min + 0.40 * (max - min);
        double mu = Math.Log(median);
        double sigma = Math.Max(0.15, (Math.Log(max) - Math.Log(min)) / 3.2);

        double z = SampleTruncatedNormal(0.0, 1.0, -2.2, 2.5);
        double sample = Math.Exp(mu + sigma * z);
        return Math.Clamp(sample, range.Min, range.Max);
    }

    private double SampleTruncatedNormal(double mean, double stdDev, double minVal, double maxVal)
    {
        if (stdDev <= 0) return mean;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = 1.0 - _rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            double val = mean + stdDev * z;
            if (val >= minVal && val <= maxVal)
            {
                return val;
            }
        }

        return Math.Clamp(mean, minVal, maxVal);
    }
}

