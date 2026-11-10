using System.Globalization;
using VmTyper.Models;
using VmTyper.Native;

namespace VmTyper.Services;

public enum KeyEventKind
{
    Down,
    Up
}

public enum KeyEncoding
{
    Scan,
    Unicode
}

public enum UnitKind
{
    Physical,
    Unicode,
    Enter,
    Tab,
    Combo,
    IndentationReset,
    Unsupported,
    PhysicalSequence
}

public readonly record struct KeyEvent(
    KeyEventKind Kind,
    KeyEncoding Enc,
    ushort Code,
    bool Extended,
    int DelayAfterMs = 0);

public sealed record InputUnit(
    int Index,
    string Text,
    UnitKind Kind,
    IReadOnlyList<KeyEvent> Events,
    string? UnsupportedReason = null,
    bool IsLineBreak = false);

public sealed record UnsupportedUnitInfo(
    int Index,
    string Category,
    string Reason);

public sealed record PlanOptions(
    InputMethod Method,
    IntPtr Layout,
    IReadOnlyList<CharacterSubstitution> Substitutions,
    bool PreserveOriginalIndentation = false,
    IndentationResetStrategy IndentationStrategy = IndentationResetStrategy.Auto,
    bool FallbackUnsupportedToUnicode = false);

public sealed record PreflightReport(
    IReadOnlyList<InputUnit> Units,
    int TotalUnits,
    int SupportedCount,
    int UnsupportedCount,
    IReadOnlyList<UnsupportedUnitInfo> Unsupported,
    IReadOnlyList<string> AppliedSubstitutionRules,
    IReadOnlyList<string> DuplicateFromWarnings);

/// <summary>
/// Planejador puro (§2.1): transforma o texto em uma sequência de unidades lógicas
/// (<see cref="InputUnit"/>) iteradas por grapheme (<see cref="StringInfo.GetTextElementEnumerator(string)"/>),
/// aplicando substituições em uma única passada sem encadeamento (N8) e sem sentinelas PUA,
/// detectando teclas mortas literais (N1) e teclas estendidas via MAPVK_VK_TO_VSC_EX (G).
/// </summary>
public sealed class KeyStrokePlanner
{
    public const int DefaultDeadKeyInterKeyDelayMs = 15;

    private readonly KeyboardMappingService _mapping;

    public KeyStrokePlanner(KeyboardMappingService mapping)
    {
        _mapping = mapping;
    }

    public static IReadOnlyList<string> FindDuplicateSubstitutionFroms(IEnumerable<CharacterSubstitution> substitutions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        foreach (var sub in substitutions)
        {
            if (string.IsNullOrEmpty(sub.From)) continue;
            if (!seen.Add(sub.From) && !duplicates.Contains(sub.From, StringComparer.Ordinal))
            {
                duplicates.Add(sub.From);
            }
        }
        return duplicates;
    }

    public PreflightReport Plan(string text, PlanOptions options)
    {
        string normalized = KeyboardInputService.NormalizeLineEndings(text);
        var validRules = new List<(int ruleNumber, CharacterSubstitution sub)>();
        for (int i = 0; i < options.Substitutions.Count; i++)
        {
            var sub = options.Substitutions[i];
            if (!string.IsNullOrEmpty(sub.From))
            {
                validRules.Add((i + 1, sub));
            }
        }

        var duplicateWarnings = FindDuplicateSubstitutionFroms(options.Substitutions);
        var appliedRulesSet = new SortedSet<int>();

        // 1. Tokenização + substituições em uma única passada, sem encadeamento e sem sentinelas PUA (N8).
        var tokens = TokenizeSinglePass(normalized, validRules, appliedRulesSet);

        // 2. Construção das unidades lógicas (InputUnit).
        var units = new List<InputUnit>();
        var unsupportedList = new List<UnsupportedUnitInfo>();
        int unitIndex = 0;

        foreach (var token in tokens)
        {
            if (token.ComboRule is { } subRule)
            {
                if (subRule.Mode == SubstitutionMode.Sequencia)
                {
                    var seqEvents = BuildPhysicalSequenceEvents(subRule.SequenceSteps, options.Layout);
                    units.Add(new InputUnit(unitIndex++, token.OriginalText, UnitKind.PhysicalSequence, seqEvents));
                    continue;
                }

                var comboEvents = BuildKeyComboEvents(
                    new KeyCombo((ushort)subRule.ComboVirtualKey, subRule.ComboShift, subRule.ComboCtrl, subRule.ComboAlt),
                    options.Layout);
                units.Add(new InputUnit(unitIndex++, token.OriginalText, UnitKind.Combo, comboEvents));
                continue;
            }

            // Expande o texto do token (original ou substituto de texto) por grapheme.
            var enumerator = StringInfo.GetTextElementEnumerator(token.Text);
            while (enumerator.MoveNext())
            {
                string grapheme = enumerator.GetTextElement();
                if (grapheme == "\n")
                {
                    var enterEvents = BuildSingleScanKeyEvents(Win32.VK_RETURN, options.Layout);
                    units.Add(new InputUnit(unitIndex++, "\n", UnitKind.Enter, enterEvents, IsLineBreak: true));

                    if (options.PreserveOriginalIndentation)
                    {
                        var resetEvents = BuildIndentationResetEvents(options.IndentationStrategy, options.Layout);
                        if (resetEvents.Count > 0)
                        {
                            // A sequência de reset de indentação é anexada após os eventos do Enter
                            // na mesma unidade lógica de quebra de linha, para manter a contagem
                            // de unidades fiéis aos caracteres lógicos do texto.
                            var combined = new List<KeyEvent>(enterEvents.Count + resetEvents.Count);
                            combined.AddRange(enterEvents);
                            combined.AddRange(resetEvents);
                            units[^1] = units[^1] with { Events = combined };
                        }
                    }
                    continue;
                }

                if (grapheme == "\t")
                {
                    var tabEvents = BuildSingleScanKeyEvents(Win32.VK_TAB, options.Layout);
                    units.Add(new InputUnit(unitIndex++, "\t", UnitKind.Tab, tabEvents));
                    continue;
                }

                var unit = PlanGrapheme(unitIndex++, grapheme, options);
                units.Add(unit);
                if (unit.Kind == UnitKind.Unsupported)
                {
                    unsupportedList.Add(new UnsupportedUnitInfo(
                        unit.Index,
                        LogService.Categorize(grapheme),
                        unit.UnsupportedReason ?? "Não suportado pelo layout de destino."));
                }
            }
        }

        var appliedRuleLabels = appliedRulesSet.Select(n => $"#{n}").ToList();
        return new PreflightReport(
            units,
            units.Count,
            units.Count - unsupportedList.Count,
            unsupportedList.Count,
            unsupportedList,
            appliedRuleLabels,
            duplicateWarnings);
    }

    private readonly record struct PlannedToken(string OriginalText, string Text, CharacterSubstitution? ComboRule);

    private static List<PlannedToken> TokenizeSinglePass(
        string text,
        List<(int ruleNumber, CharacterSubstitution sub)> rules,
        SortedSet<int> appliedRules)
    {
        var result = new List<PlannedToken>();
        if (string.IsNullOrEmpty(text)) return result;

        int pos = 0;
        while (pos < text.Length)
        {
            (int ruleNumber, CharacterSubstitution sub)? matched = null;
            foreach (var entry in rules)
            {
                string from = entry.sub.From;
                if (pos + from.Length <= text.Length &&
                    string.CompareOrdinal(text, pos, from, 0, from.Length) == 0)
                {
                    matched = entry;
                    break;
                }
            }

            if (matched is { } m)
            {
                appliedRules.Add(m.ruleNumber);
                if (m.sub.Mode is SubstitutionMode.Combinacao or SubstitutionMode.Sequencia)
                {
                    result.Add(new PlannedToken(m.sub.From, string.Empty, m.sub));
                }
                else if (!string.IsNullOrEmpty(m.sub.To))
                {
                    result.Add(new PlannedToken(m.sub.From, m.sub.To, null));
                }
                pos += m.sub.From.Length;
            }
            else
            {
                string elem = StringInfo.GetNextTextElement(text, pos);
                result.Add(new PlannedToken(elem, elem, null));
                pos += elem.Length;
            }
        }

        return result;
    }

    private InputUnit PlanGrapheme(int index, string grapheme, PlanOptions options)
    {
        if (options.Method == InputMethod.Unicode)
        {
            return new InputUnit(index, grapheme, UnitKind.Unicode, BuildUnicodeEvents(grapheme));
        }

        // Teclado Físico só resolve unidades de 1 char BMP (ou combinadas via NFD em 1 char).
        if (grapheme.Length == 1)
        {
            var mapping = _mapping.Resolve(grapheme[0], options.Layout);
            if (mapping.Success)
            {
                var events = new List<KeyEvent>();
                for (int comboIdx = 0; comboIdx < mapping.Combos.Count; comboIdx++)
                {
                    var combo = mapping.Combos[comboIdx];
                    var comboEvents = BuildKeyComboEvents(combo, options.Layout);
                    // Se este combo é uma tecla morta que precede outro combo (letra base),
                    // insere o intervalo inter-teclas no KeyUp para o console e o SO processarem a tecla morta.
                    if (comboIdx < mapping.Combos.Count - 1 && comboEvents.Count > 0)
                    {
                        int lastIdx = comboEvents.Count - 1;
                        var last = comboEvents[lastIdx];
                        comboEvents[lastIdx] = last with { DelayAfterMs = Math.Max(last.DelayAfterMs, DefaultDeadKeyInterKeyDelayMs) };
                    }
                    events.AddRange(comboEvents);
                }
                return new InputUnit(index, grapheme, UnitKind.Physical, events);
            }

            if (options.Method == InputMethod.Automatico || options.FallbackUnsupportedToUnicode)
            {
                return new InputUnit(index, grapheme, UnitKind.Unicode, BuildUnicodeEvents(grapheme));
            }

            return new InputUnit(
                index,
                grapheme,
                UnitKind.Unsupported,
                Array.Empty<KeyEvent>(),
                mapping.FailureReason ?? "Caractere não suportado pelo layout de teclado de destino.");
        }

        // Grapheme multi-char (par substituto / emoji / sequência combinante):
        if (options.Method == InputMethod.Automatico || options.FallbackUnsupportedToUnicode)
        {
            return new InputUnit(index, grapheme, UnitKind.Unicode, BuildUnicodeEvents(grapheme));
        }

        return new InputUnit(
            index,
            grapheme,
            UnitKind.Unsupported,
            Array.Empty<KeyEvent>(),
            "Par substituto ou sequência multi-code-unit exige modo Automático ou Unicode.");
    }

    private List<KeyEvent> BuildSingleScanKeyEvents(ushort vk, IntPtr layout)
    {
        var (scan, ext) = _mapping.ResolveScan(vk, layout);
        return new List<KeyEvent>(2)
        {
            new(KeyEventKind.Down, KeyEncoding.Scan, scan, ext),
            new(KeyEventKind.Up, KeyEncoding.Scan, scan, ext)
        };
    }

    private List<KeyEvent> BuildKeyComboEvents(KeyCombo combo, IntPtr layout)
    {
        var downs = new List<KeyEvent>(4);
        var ups = new List<KeyEvent>(4);

        void AddMod(ushort vk)
        {
            var (s, e) = _mapping.ResolveScan(vk, layout);
            downs.Add(new KeyEvent(KeyEventKind.Down, KeyEncoding.Scan, s, e));
            ups.Insert(0, new KeyEvent(KeyEventKind.Up, KeyEncoding.Scan, s, e));
        }

        if (combo.Shift)
        {
            AddMod(Win32.VK_LSHIFT);
        }

        if (combo.Ctrl && combo.Alt)
        {
            AddMod(Win32.VK_LCONTROL);
            AddMod(Win32.VK_RMENU);
        }
        else if (combo.Ctrl)
        {
            AddMod(Win32.VK_LCONTROL);
        }
        else if (combo.Alt)
        {
            AddMod(Win32.VK_LMENU);
        }

        var (mainScan, mainExt) = _mapping.ResolveScan(combo.VirtualKey, layout);
        downs.Add(new KeyEvent(KeyEventKind.Down, KeyEncoding.Scan, mainScan, mainExt));
        ups.Insert(0, new KeyEvent(KeyEventKind.Up, KeyEncoding.Scan, mainScan, mainExt));

        var all = new List<KeyEvent>(downs.Count + ups.Count);
        all.AddRange(downs);
        all.AddRange(ups);
        return all;
    }

    private List<KeyEvent> BuildPhysicalSequenceEvents(IReadOnlyList<KeySequenceStep> steps, IntPtr layout)
    {
        var events = new List<KeyEvent>();
        if (steps == null || steps.Count == 0) return events;

        foreach (var step in steps)
        {
            ushort scan = step.ScanCode;
            bool ext = step.Extended;
            if (scan == 0 && step.VirtualKey > 0)
            {
                var (resolvedScan, resolvedExt) = _mapping.ResolveScan(step.VirtualKey, layout);
                scan = resolvedScan;
                ext = step.Extended || resolvedExt;
            }

            var mods = new List<(ushort scan, bool ext)>();
            if (step.Shift)
            {
                var (ms, me) = _mapping.ResolveScan(Win32.VK_LSHIFT, layout);
                mods.Add((ms, me));
            }
            if (step.AltGr)
            {
                var (cs, ce) = _mapping.ResolveScan(Win32.VK_LCONTROL, layout);
                var (as_, ae) = _mapping.ResolveScan(Win32.VK_RMENU, layout);
                mods.Add((cs, ce));
                mods.Add((as_, ae));
            }
            else
            {
                if (step.Ctrl)
                {
                    var (cs, ce) = _mapping.ResolveScan(Win32.VK_LCONTROL, layout);
                    mods.Add((cs, ce));
                }
                if (step.Alt)
                {
                    var (as_, ae) = _mapping.ResolveScan(Win32.VK_LMENU, layout);
                    mods.Add((as_, ae));
                }
            }

            switch (step.Action)
            {
                case KeySequenceAction.Press:
                    foreach (var (ms, me) in mods)
                    {
                        events.Add(new KeyEvent(KeyEventKind.Down, KeyEncoding.Scan, ms, me));
                    }
                    events.Add(new KeyEvent(KeyEventKind.Down, KeyEncoding.Scan, scan, ext));
                    events.Add(new KeyEvent(KeyEventKind.Up, KeyEncoding.Scan, scan, ext, DelayAfterMs: step.DelayMs));
                    for (int mIdx = mods.Count - 1; mIdx >= 0; mIdx--)
                    {
                        events.Add(new KeyEvent(KeyEventKind.Up, KeyEncoding.Scan, mods[mIdx].scan, mods[mIdx].ext));
                    }
                    break;

                case KeySequenceAction.KeyDown:
                    foreach (var (ms, me) in mods)
                    {
                        events.Add(new KeyEvent(KeyEventKind.Down, KeyEncoding.Scan, ms, me));
                    }
                    events.Add(new KeyEvent(KeyEventKind.Down, KeyEncoding.Scan, scan, ext, DelayAfterMs: step.DelayMs));
                    break;

                case KeySequenceAction.KeyUp:
                    events.Add(new KeyEvent(KeyEventKind.Up, KeyEncoding.Scan, scan, ext, DelayAfterMs: step.DelayMs));
                    for (int mIdx = mods.Count - 1; mIdx >= 0; mIdx--)
                    {
                        events.Add(new KeyEvent(KeyEventKind.Up, KeyEncoding.Scan, mods[mIdx].scan, mods[mIdx].ext));
                    }
                    break;
            }
        }

        return events;
    }

    /// <summary>
    /// Constrói eventos Unicode. Para pares substitutos (2 code units UTF-16):
    /// down alto, down baixo, up baixo, up alto (§2.1).
    /// </summary>
    public static IReadOnlyList<KeyEvent> BuildUnicodeEvents(string grapheme)
    {
        if (grapheme.Length == 1)
        {
            ushort code = grapheme[0];
            return new KeyEvent[]
            {
                new(KeyEventKind.Down, KeyEncoding.Unicode, code, false),
                new(KeyEventKind.Up, KeyEncoding.Unicode, code, false)
            };
        }

        if (grapheme.Length == 2 && char.IsHighSurrogate(grapheme[0]) && char.IsLowSurrogate(grapheme[1]))
        {
            ushort high = grapheme[0];
            ushort low = grapheme[1];
            return new KeyEvent[]
            {
                new(KeyEventKind.Down, KeyEncoding.Unicode, high, false),
                new(KeyEventKind.Down, KeyEncoding.Unicode, low, false),
                new(KeyEventKind.Up, KeyEncoding.Unicode, low, false),
                new(KeyEventKind.Up, KeyEncoding.Unicode, high, false)
            };
        }

        // Sequência combinante multi-char genérica: envia cada code unit em pares balanceados.
        var list = new List<KeyEvent>(grapheme.Length * 2);
        foreach (char c in grapheme)
        {
            list.Add(new KeyEvent(KeyEventKind.Down, KeyEncoding.Unicode, c, false));
            list.Add(new KeyEvent(KeyEventKind.Up, KeyEncoding.Unicode, c, false));
        }
        return list;
    }

    private List<KeyEvent> BuildIndentationResetEvents(IndentationResetStrategy strategy, IntPtr layout)
    {
        var effective = strategy == IndentationResetStrategy.Auto
            ? IndentationResetStrategy.ShiftHomeDelete
            : strategy;

        return effective switch
        {
            IndentationResetStrategy.ShiftHomeDelete =>
                BuildKeyComboEvents(new KeyCombo(Win32.VK_HOME, shift: true, ctrl: false, alt: false), layout),
            IndentationResetStrategy.DoubleHome =>
                BuildSingleScanKeyEvents(Win32.VK_HOME, layout)
                    .Concat(BuildSingleScanKeyEvents(Win32.VK_HOME, layout))
                    .ToList(),
            _ => new List<KeyEvent>()
        };
    }
}
