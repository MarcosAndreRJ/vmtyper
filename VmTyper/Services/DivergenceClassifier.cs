using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VmTyper.Services;

public enum DivergenceKind
{
    Duplication,
    Omission,
    Substitution,
    AccentuationFailure,
    Transposition,
    IndentationDivergence
}

public sealed record DivergenceItem(
    DivergenceKind Kind,
    int ExpectedIndex,
    int ActualIndex,
    string ExpectedCategory,
    string ActualCategory,
    string ContextWindow,
    DiagnosticEventRecord? CorrelatedEvent = null);

public sealed record TextFingerprint(
    int CharLength,
    int GraphemeCount,
    string Sha256Hex12,
    IReadOnlyDictionary<string, int> CategoryCounts);

public sealed record DivergenceReport(
    int ExpectedGraphemes,
    int ActualGraphemes,
    bool IsIdentical,
    int Duplications,
    int Omissions,
    int Substitutions,
    int AccentuationFailures,
    int Transpositions,
    int IndentationDivergences,
    double DivergenceRatePer10k,
    IReadOnlyList<DivergenceItem> Items)
{
    public int TotalDivergences =>
        Duplications + Omissions + Substitutions + AccentuationFailures + Transpositions + IndentationDivergences;
}

/// <summary>
/// Classificador puro de divergências por alinhamento de Myers em nível de grapheme (Fase 3 §5).
/// Por padrão, mascara o contexto (±5 unidades) por categoria; só inclui graphemes literais
/// quando <c>detailedTrace</c> está explicitamente habilitado.
/// </summary>
public static class DivergenceClassifier
{
    private static readonly HashSet<string> IsolatedAccents = new(StringComparer.Ordinal)
    {
        "~", "^", "´", "`", "¨", "'", "\""
    };

    public static DivergenceReport Classify(
        string expectedText,
        string actualText,
        IReadOnlyList<DiagnosticEventRecord>? sessionEvents = null,
        bool detailedTrace = false)
    {
        var exp = SplitGraphemes(KeyboardInputService.NormalizeLineEndings(expectedText));
        var act = SplitGraphemes(KeyboardInputService.NormalizeLineEndings(actualText));

        var eventByIndex = sessionEvents?.ToDictionary(e => e.Index) ?? new Dictionary<int, DiagnosticEventRecord>();
        var ops = ComputeMyersEditScript(exp, act);

        var items = new List<DivergenceItem>();
        int duplications = 0;
        int omissions = 0;
        int substitutions = 0;
        int accentFailures = 0;
        int transpositions = 0;
        int indentationDivs = 0;

        var leadingPositionsExp = ComputeLeadingWhitespaceMask(exp);
        var leadingPositionsAct = ComputeLeadingWhitespaceMask(act);

        int idx = 0;
        while (idx < ops.Count)
        {
            var op = ops[idx];
            if (op.Type == EditOpType.Equal)
            {
                idx++;
                continue;
            }

            // 1. Falha de acentuação do tipo acento isolado + letra base (ex.: esperado "ã", recebido "~" + "a"):
            // No script de edição de Myers aparece como 3 operações:
            // Delete(exp[i]="ã"), Insert(act[j]="~"), Insert(act[j+1]="a")
            // ou Insert(act[j]="~"), Delete(exp[i]="ã"), Insert(act[j+1]="a").
            if (idx + 2 < ops.Count && IsSplitAccentSequence(ops[idx], ops[idx + 1], ops[idx + 2], exp, act, out int expAccentIdx, out int actAccentIdx))
            {
                accentFailures++;
                items.Add(BuildItem(
                    DivergenceKind.AccentuationFailure,
                    expAccentIdx,
                    actAccentIdx,
                    exp,
                    act,
                    eventByIndex,
                    detailedTrace));
                idx += 3;
                continue;
            }

            // Transposição adjacente clássica em Myers: Delete(exp[i]=A), Equal(exp[i+1]=B, act[j]=B), Insert(act[j+1]=A)
            // ou Insert(act[j]=B), Equal(exp[i]=A, act[j+1]=A), Delete(exp[i+1]=B)
            if (idx + 2 < ops.Count && IsMyersAdjacentSwap(ops[idx], ops[idx + 1], ops[idx + 2], exp, act, out int swapE, out int swapA))
            {
                transpositions++;
                items.Add(BuildItem(DivergenceKind.Transposition, swapE, swapA, exp, act, eventByIndex, detailedTrace));
                idx += 3;
                continue;
            }

            // Bloco contíguo de Deletes seguido de Inserts (Substituição / Acentuação / Indentação)
            if (op.Type == EditOpType.Delete)
            {
                int delStart = idx;
                while (idx < ops.Count && ops[idx].Type == EditOpType.Delete) idx++;
                int delCount = idx - delStart;

                int insStart = idx;
                while (idx < ops.Count && ops[idx].Type == EditOpType.Insert) idx++;
                int insCount = idx - insStart;

                if (insCount == 0)
                {
                    for (int d = 0; d < delCount; d++)
                    {
                        var delOp = ops[delStart + d];
                        int eIdx = delOp.ExpectedIndex;
                        int aIdx = Math.Clamp(delOp.ActualIndex, 0, Math.Max(0, act.Count - 1));
                        string deleted = exp[eIdx];

                        if (IsWhitespaceOnly(deleted) && leadingPositionsExp[eIdx])
                        {
                            indentationDivs++;
                            items.Add(BuildItem(DivergenceKind.IndentationDivergence, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                        }
                        else
                        {
                            omissions++;
                            items.Add(BuildItem(DivergenceKind.Omission, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                        }
                    }
                    continue;
                }

                // Se todos os graphemes removidos e inseridos no bloco são espaços/tabs de indentação no início da linha:
                bool allIndent = true;
                for (int d = 0; d < delCount; d++)
                {
                    int eIdx = ops[delStart + d].ExpectedIndex;
                    if (!IsWhitespaceOnly(exp[eIdx]) || !leadingPositionsExp[eIdx]) { allIndent = false; break; }
                }
                if (allIndent)
                {
                    for (int ins = 0; ins < insCount; ins++)
                    {
                        int aIdx = ops[insStart + ins].ActualIndex;
                        if (!IsWhitespaceOnly(act[aIdx]) || !leadingPositionsAct[aIdx]) { allIndent = false; break; }
                    }
                }
                if (allIndent)
                {
                    indentationDivs++;
                    items.Add(BuildItem(
                        DivergenceKind.IndentationDivergence,
                        ops[delStart].ExpectedIndex,
                        ops[insStart].ActualIndex,
                        exp,
                        act,
                        eventByIndex,
                        detailedTrace));
                    continue;
                }

                int paired = Math.Min(delCount, insCount);
                for (int p = 0; p < paired; p++)
                {
                    int eIdx = ops[delStart + p].ExpectedIndex;
                    int aIdx = ops[insStart + p].ActualIndex;
                    string eGraph = exp[eIdx];
                    string aGraph = act[aIdx];

                    if (IsWhitespaceOnly(eGraph) && IsWhitespaceOnly(aGraph) &&
                        (leadingPositionsExp[eIdx] || leadingPositionsAct[aIdx]))
                    {
                        indentationDivs++;
                        items.Add(BuildItem(DivergenceKind.IndentationDivergence, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                    }
                    else if (IsAccentuationPair(eGraph, aGraph))
                    {
                        accentFailures++;
                        items.Add(BuildItem(DivergenceKind.AccentuationFailure, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                    }
                    else
                    {
                        substitutions++;
                        items.Add(BuildItem(DivergenceKind.Substitution, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                    }
                }

                for (int d = paired; d < delCount; d++)
                {
                    int eIdx = ops[delStart + d].ExpectedIndex;
                    int aIdx = Math.Clamp(ops[delStart + d].ActualIndex, 0, Math.Max(0, act.Count - 1));
                    omissions++;
                    items.Add(BuildItem(DivergenceKind.Omission, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                }

                for (int ins = paired; ins < insCount; ins++)
                {
                    int aIdx = ops[insStart + ins].ActualIndex;
                    int eIdx = Math.Clamp(ops[insStart + ins].ExpectedIndex, 0, Math.Max(0, exp.Count - 1));
                    string inserted = act[aIdx];
                    bool isDupPrev = aIdx > 0 && act[aIdx - 1] == inserted;
                    bool isDupNext = aIdx + 1 < act.Count && act[aIdx + 1] == inserted;
                    if (isDupPrev || isDupNext)
                    {
                        duplications++;
                        items.Add(BuildItem(DivergenceKind.Duplication, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                    }
                    else
                    {
                        substitutions++;
                        items.Add(BuildItem(DivergenceKind.Substitution, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                    }
                }
                continue;
            }

            if (op.Type == EditOpType.Insert)
            {
                int aIdx = op.ActualIndex;
                int eIdx = Math.Clamp(op.ExpectedIndex, 0, Math.Max(0, exp.Count - 1));
                string inserted = act[aIdx];

                bool isDupPrev = aIdx > 0 && act[aIdx - 1] == inserted;
                bool isDupNext = aIdx + 1 < act.Count && act[aIdx + 1] == inserted;
                bool isDupExp = exp.Count > 0 && exp[eIdx] == inserted;

                if (isDupPrev || isDupNext || isDupExp)
                {
                    if (!isDupExp && eIdx > 0 && exp[eIdx - 1] == inserted)
                    {
                        eIdx--;
                    }
                    duplications++;
                    items.Add(BuildItem(DivergenceKind.Duplication, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                }
                else if (IsWhitespaceOnly(inserted) && leadingPositionsAct[aIdx])
                {
                    indentationDivs++;
                    items.Add(BuildItem(DivergenceKind.IndentationDivergence, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                }
                else
                {
                    substitutions++;
                    items.Add(BuildItem(DivergenceKind.Substitution, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                }
                idx++;
                continue;
            }

            if (op.Type == EditOpType.Delete)
            {
                int eIdx = op.ExpectedIndex;
                int aIdx = Math.Clamp(op.ActualIndex, 0, Math.Max(0, act.Count - 1));
                string deleted = exp[eIdx];

                if (IsWhitespaceOnly(deleted) && leadingPositionsExp[eIdx])
                {
                    indentationDivs++;
                    items.Add(BuildItem(DivergenceKind.IndentationDivergence, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                }
                else
                {
                    omissions++;
                    items.Add(BuildItem(DivergenceKind.Omission, eIdx, aIdx, exp, act, eventByIndex, detailedTrace));
                }
                idx++;
                continue;
            }

            idx++;
        }

        int totalDiv = duplications + omissions + substitutions + accentFailures + transpositions + indentationDivs;
        double ratePer10k = exp.Count > 0 ? Math.Round(totalDiv * 10_000.0 / exp.Count, 2) : 0.0;

        return new DivergenceReport(
            ExpectedGraphemes: exp.Count,
            ActualGraphemes: act.Count,
            IsIdentical: totalDiv == 0 && exp.Count == act.Count,
            Duplications: duplications,
            Omissions: omissions,
            Substitutions: substitutions,
            AccentuationFailures: accentFailures,
            Transpositions: transpositions,
            IndentationDivergences: indentationDivs,
            DivergenceRatePer10k: ratePer10k,
            Items: items);
    }

    public static TextFingerprint ComputeFingerprint(string text)
    {
        string normalized = KeyboardInputService.NormalizeLineEndings(text);
        var graphemes = SplitGraphemes(normalized);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string hex12 = Convert.ToHexString(hash)[..12].ToLowerInvariant();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["letra"] = 0,
            ["acentuado"] = 0,
            ["dígito"] = 0,
            ["espaço"] = 0,
            ["pontuação"] = 0,
            ["controle"] = 0
        };

        foreach (string g in graphemes)
        {
            string cat = LogService.Categorize(g);
            counts[cat] = counts.TryGetValue(cat, out int c) ? c + 1 : 1;
        }

        return new TextFingerprint(normalized.Length, graphemes.Count, hex12, counts);
    }

    public static List<string> SplitGraphemes(string text)
    {
        var list = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            list.Add(enumerator.GetTextElement());
        }
        return list;
    }

    private static DivergenceItem BuildItem(
        DivergenceKind kind,
        int expIdx,
        int actIdx,
        IReadOnlyList<string> exp,
        IReadOnlyList<string> act,
        IReadOnlyDictionary<int, DiagnosticEventRecord> eventByIndex,
        bool detailedTrace)
    {
        string expCat = expIdx >= 0 && expIdx < exp.Count ? LogService.Categorize(exp[expIdx]) : "Nenhum";
        string actCat = actIdx >= 0 && actIdx < act.Count ? LogService.Categorize(act[actIdx]) : "Nenhum";

        int start = Math.Max(0, expIdx - 5);
        int end = Math.Min(exp.Count - 1, expIdx + 5);
        var ctxParts = new List<string>();
        for (int i = start; i <= end; i++)
        {
            string token = detailedTrace ? FormatLiteral(exp[i]) : LogService.Categorize(exp[i]);
            ctxParts.Add(i == expIdx ? $"[{token}]" : token);
        }

        eventByIndex.TryGetValue(expIdx, out var correlated);
        return new DivergenceItem(
            kind,
            expIdx,
            actIdx,
            expCat,
            actCat,
            string.Join(" ", ctxParts),
            correlated);
    }

    private static string FormatLiteral(string g) => g switch
    {
        "\n" => "\\n",
        "\t" => "\\t",
        " " => "␣",
        _ => g
    };

    private static bool IsWhitespaceOnly(string g) => g is " " or "\t";

    private static bool[] ComputeLeadingWhitespaceMask(IReadOnlyList<string> graphemes)
    {
        var mask = new bool[graphemes.Count];
        bool atLineStart = true;
        for (int i = 0; i < graphemes.Count; i++)
        {
            string g = graphemes[i];
            if (g == "\n")
            {
                atLineStart = true;
            }
            else if (atLineStart && (g == " " || g == "\t"))
            {
                mask[i] = true;
            }
            else
            {
                atLineStart = false;
            }
        }
        return mask;
    }

    private static bool IsAccentuationPair(string expected, string actual)
    {
        if (expected == actual) return false;
        string baseE = StripDiacritics(expected);
        string baseA = StripDiacritics(actual);
        return !string.IsNullOrEmpty(baseE) && string.Equals(baseE, baseA, StringComparison.Ordinal);
    }

    private static bool IsSplitAccentSequence(
        EditOp first,
        EditOp second,
        EditOp third,
        IReadOnlyList<string> exp,
        IReadOnlyList<string> act,
        out int expIdx,
        out int actIdx)
    {
        expIdx = 0;
        actIdx = 0;

        // Padrão A: Delete(exp="ã"), Insert(act="~"), Insert(act="a")
        if (first.Type == EditOpType.Delete &&
            second.Type == EditOpType.Insert &&
            third.Type == EditOpType.Insert &&
            HasDiacritic(exp[first.ExpectedIndex]) &&
            IsolatedAccents.Contains(act[second.ActualIndex]) &&
            string.Equals(StripDiacritics(exp[first.ExpectedIndex]), act[third.ActualIndex], StringComparison.Ordinal))
        {
            expIdx = first.ExpectedIndex;
            actIdx = second.ActualIndex;
            return true;
        }

        // Padrão B: Insert(act="~"), Delete(exp="ã"), Insert(act="a")
        if (first.Type == EditOpType.Insert &&
            second.Type == EditOpType.Delete &&
            third.Type == EditOpType.Insert &&
            IsolatedAccents.Contains(act[first.ActualIndex]) &&
            HasDiacritic(exp[second.ExpectedIndex]) &&
            string.Equals(StripDiacritics(exp[second.ExpectedIndex]), act[third.ActualIndex], StringComparison.Ordinal))
        {
            expIdx = second.ExpectedIndex;
            actIdx = first.ActualIndex;
            return true;
        }

        return false;
    }

    private static bool IsMyersAdjacentSwap(
        EditOp op1,
        EditOp op2,
        EditOp op3,
        IReadOnlyList<string> exp,
        IReadOnlyList<string> act,
        out int swapExpIdx,
        out int swapActIdx)
    {
        swapExpIdx = 0;
        swapActIdx = 0;

        // Padrão 1: Delete(exp[i]=A), Equal(exp[i+1]=B, act[j]=B), Insert(act[j+1]=A)
        if (op1.Type == EditOpType.Delete &&
            op2.Type == EditOpType.Equal &&
            op3.Type == EditOpType.Insert &&
            op2.ExpectedIndex == op1.ExpectedIndex + 1 &&
            exp[op1.ExpectedIndex] == act[op3.ActualIndex])
        {
            swapExpIdx = op1.ExpectedIndex;
            swapActIdx = op2.ActualIndex;
            return true;
        }

        // Padrão 2: Insert(act[j]=B), Equal(exp[i]=A, act[j+1]=A), Delete(exp[i+1]=B)
        if (op1.Type == EditOpType.Insert &&
            op2.Type == EditOpType.Equal &&
            op3.Type == EditOpType.Delete &&
            op3.ExpectedIndex == op2.ExpectedIndex + 1 &&
            act[op1.ActualIndex] == exp[op3.ExpectedIndex])
        {
            swapExpIdx = op2.ExpectedIndex;
            swapActIdx = op1.ActualIndex;
            return true;
        }

        return false;
    }

    private static bool HasDiacritic(string s)
    {
        string norm = s.Normalize(NormalizationForm.FormD);
        foreach (char c in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                return true;
        }
        return false;
    }

    public static string StripDiacritics(string s)
    {
        string norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (char c in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private enum EditOpType
    {
        Equal,
        Insert,
        Delete
    }

    private readonly record struct EditOp(EditOpType Type, int ExpectedIndex, int ActualIndex);

    /// <summary>Algoritmo de diff de Myers O((N+M)D) em nível de grapheme.</summary>
    private static List<EditOp> ComputeMyersEditScript(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count;
        int m = b.Count;
        int max = n + m;
        if (max == 0) return new List<EditOp>();

        int offset = max;
        int[] v = new int[2 * max + 1];
        var trace = new List<int[]>(max + 1);

        for (int d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                {
                    x = v[offset + k + 1];
                }
                else
                {
                    x = v[offset + k - 1] + 1;
                }

                int y = x - k;
                while (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    return BacktrackMyers(trace, a, b, offset, d);
                }
            }
        }

        return new List<EditOp>();
    }

    private static List<EditOp> BacktrackMyers(
        List<int[]> trace,
        IReadOnlyList<string> a,
        IReadOnlyList<string> b,
        int offset,
        int finalD)
    {
        var ops = new List<EditOp>(a.Count + b.Count);
        int x = a.Count;
        int y = b.Count;

        for (int d = finalD; d > 0; d--)
        {
            int[] v = trace[d];
            int k = x - y;
            int prevK;
            if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
            {
                prevK = k + 1;
            }
            else
            {
                prevK = k - 1;
            }

            int prevX = v[offset + prevK];
            int prevY = prevX - prevK;

            while (x > prevX && y > prevY)
            {
                x--;
                y--;
                ops.Add(new EditOp(EditOpType.Equal, x, y));
            }

            if (x == prevX)
            {
                y--;
                ops.Add(new EditOp(EditOpType.Insert, x, y));
            }
            else
            {
                x--;
                ops.Add(new EditOp(EditOpType.Delete, x, y));
            }
        }

        while (x > 0 && y > 0)
        {
            x--;
            y--;
            ops.Add(new EditOp(EditOpType.Equal, x, y));
        }

        ops.Reverse();
        return ops;
    }
}
