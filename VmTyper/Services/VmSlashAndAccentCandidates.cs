using VmTyper.Models;
using VmTyper.Native;

namespace VmTyper.Services;

/// <summary>
/// Representa a definição de um candidato de digitação física (combinação ou sequência)
/// para contornar problemas de layout no console VMware HTML5 (§Tarefas 1, 3 e 4).
/// </summary>
public sealed record CandidateOption(
    string Id,
    string Name,
    string TechnicalDetails,
    SubstitutionMode Mode,
    CharacterSubstitution SubstitutionTemplate);

/// <summary>
/// Catálogo e gerador de candidatos de teclas físicas para caracteres críticos na VM
/// (barra '/', interrogação '?', acentuação 'á, à, â, ã, é, ê, í, ó, ô, õ, ú' e maiúsculas).
/// </summary>
public static class VmSlashAndAccentCandidates
{
    public static readonly IReadOnlyList<string> RecommendedSymbols = new[]
    {
        "/", "?", "á", "ã", "é", "ê", "í", "ó", "ô", "õ", "ú", "ç",
        "Á", "Ã", "É", "Ê", "Í", "Ó", "Ô", "Õ", "Ú", "Ç"
    };

    public static IReadOnlyList<CandidateOption> GetCandidatesForSymbol(string symbol) => symbol switch
    {
        "/" => GetSlashCandidates(),
        "?" => GetQuestionCandidates(),
        "á" => GetAcuteCandidates("a", isUpper: false, asciiCode: 160),
        "Á" => GetAcuteCandidates("A", isUpper: true, asciiCode: 181),
        "à" => GetGraveCandidates("a", isUpper: false, asciiCode: 133),
        "À" => GetGraveCandidates("A", isUpper: true, asciiCode: 0),
        "ã" => GetTildeCandidates("a", isUpper: false, asciiCode: 198),
        "Ã" => GetTildeCandidates("A", isUpper: true, asciiCode: 199),
        "â" => GetCircumflexCandidates("a", isUpper: false, asciiCode: 131),
        "Â" => GetCircumflexCandidates("A", isUpper: true, asciiCode: 0),
        "é" => GetAcuteCandidates("e", isUpper: false, asciiCode: 130),
        "É" => GetAcuteCandidates("E", isUpper: true, asciiCode: 144),
        "ê" => GetCircumflexCandidates("e", isUpper: false, asciiCode: 136),
        "Ê" => GetCircumflexCandidates("E", isUpper: true, asciiCode: 0),
        "í" => GetAcuteCandidates("i", isUpper: false, asciiCode: 161),
        "Í" => GetAcuteCandidates("I", isUpper: true, asciiCode: 214),
        "ó" => GetAcuteCandidates("o", isUpper: false, asciiCode: 162),
        "Ó" => GetAcuteCandidates("O", isUpper: true, asciiCode: 224),
        "ô" => GetCircumflexCandidates("o", isUpper: false, asciiCode: 147),
        "Ô" => GetCircumflexCandidates("O", isUpper: true, asciiCode: 0),
        "õ" => GetTildeCandidates("o", isUpper: false, asciiCode: 228),
        "Õ" => GetTildeCandidates("O", isUpper: true, asciiCode: 229),
        "ú" => GetAcuteCandidates("u", isUpper: false, asciiCode: 163),
        "Ú" => GetAcuteCandidates("U", isUpper: true, asciiCode: 233),
        "ç" => GetCedillaCandidates(isUpper: false),
        "Ç" => GetCedillaCandidates(isUpper: true),
        _ => GetGenericFallbackCandidates(symbol)
    };

    public static IReadOnlyList<CandidateOption> GetSlashCandidates()
    {
        return new[]
        {
            new CandidateOption(
                "NumpadDivide",
                "Barra do teclado numérico (VK_DIVIDE 0x6F, Scan 0x35, Estendida E0)",
                "VirtualKey=0x6F, ScanCode=0x35, Extended=True, Modifiers=None. " +
                "Gera event.code='NumpadDivide' no navegador.",
                SubstitutionMode.Combinacao,
                new CharacterSubstitution
                {
                    From = "/",
                    Mode = SubstitutionMode.Combinacao,
                    ComboVirtualKey = Win32.VK_DIVIDE,
                    ComboLabel = "Barra (numpad / divide E0 35)"
                }),

            new CandidateOption(
                "Abnt2Slash",
                "Tecla ABNT2 próxima ao Shift direito (VK_ABNT_C1 0xC1, Scan 0x73)",
                "VirtualKey=0xC1, ScanCode=0x73, Extended=False. Tecla física do layout brasileiro ABNT2. " +
                "Gera code='IntlRo' no navegador.",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = "/",
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { VirtualKey = Win32.VK_ABNT_C1, ScanCode = 0x73, Extended = false, Action = KeySequenceAction.Press, Label = "ABNT2 Slash (Scan 0x73)" }
                    }
                }),

            new CandidateOption(
                "UsSlash",
                "Barra padrão teclado US (VK_OEM_2 0xBF, Scan 0x35, Não-estendida)",
                "VirtualKey=0xBF, ScanCode=0x35, Extended=False. Tecla física de 101/104 teclas ao lado do Shift. " +
                "Gera code='Slash' no navegador.",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = "/",
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { VirtualKey = Win32.VK_OEM_2, ScanCode = 0x35, Extended = false, Action = KeySequenceAction.Press, Label = "US Slash (Scan 0x35)" }
                    }
                }),

            new CandidateOption(
                "AltGrQ",
                "Combinação AltGr + Q (Ctrl + Alt + Q)",
                "VirtualKey=0x51 ('Q'), ScanCode=0x10, Modificadores: Ctrl + Alt (AltGr). " +
                "Produz '/' em vários terminais e layouts com mapeamento secundário.",
                SubstitutionMode.Combinacao,
                new CharacterSubstitution
                {
                    From = "/",
                    Mode = SubstitutionMode.Combinacao,
                    ComboCtrl = true,
                    ComboAlt = true,
                    ComboVirtualKey = 0x51,
                    ComboLabel = "AltGr + Q"
                }),

            new CandidateOption(
                "AltNumpad47",
                "Sequência física Alt + 47 no teclado numérico",
                "Passo 1: Alt Down -> Passo 2: Numpad 4 (Scan 0x4B) -> Passo 3: Numpad 7 (Scan 0x47) -> Passo 4: Alt Up. " +
                "Produz '/' (ASCII 47) universalmente em caixas de texto do Windows.",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = "/",
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = BuildAltNumpadSteps(47, "/")
                })
        };
    }

    public static IReadOnlyList<CandidateOption> GetQuestionCandidates()
    {
        return new[]
        {
            new CandidateOption(
                "Abnt2Question",
                "Tecla ABNT2 Shift + / (Shift + Scan 0x73)",
                "VirtualKey=0xC1, ScanCode=0x73, Shift=True. Produz '?' no teclado ABNT2.",
                SubstitutionMode.Combinacao,
                new CharacterSubstitution
                {
                    From = "?",
                    Mode = SubstitutionMode.Combinacao,
                    ComboShift = true,
                    ComboVirtualKey = Win32.VK_ABNT_C1,
                    ComboLabel = "Shift + ABNT2 Slash (0x73)"
                }),

            new CandidateOption(
                "UsQuestion",
                "Teclado US Shift + / (Shift + Scan 0x35)",
                "VirtualKey=0xBF, ScanCode=0x35, Shift=True. Produz '?' no teclado padrão US.",
                SubstitutionMode.Combinacao,
                new CharacterSubstitution
                {
                    From = "?",
                    Mode = SubstitutionMode.Combinacao,
                    ComboShift = true,
                    ComboVirtualKey = Win32.VK_OEM_2,
                    ComboLabel = "Shift + US Slash (0x35)"
                }),

            new CandidateOption(
                "AltGrW",
                "Combinação AltGr + W (ABNT2 ? alternativo)",
                "VirtualKey=0x57 ('W'), ScanCode=0x11, Modificadores: AltGr (Ctrl + Alt).",
                SubstitutionMode.Combinacao,
                new CharacterSubstitution
                {
                    From = "?",
                    Mode = SubstitutionMode.Combinacao,
                    ComboCtrl = true,
                    ComboAlt = true,
                    ComboVirtualKey = 0x57,
                    ComboLabel = "AltGr + W"
                }),

            new CandidateOption(
                "AltNumpad63",
                "Sequência física Alt + 63 no teclado numérico",
                "Alt Down -> Numpad 6 -> Numpad 3 -> Alt Up. Produz '?' (ASCII 63).",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = "?",
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = BuildAltNumpadSteps(63, "?")
                })
        };
    }

    private static IReadOnlyList<CandidateOption> GetAcuteCandidates(string letter, bool isUpper, int asciiCode)
    {
        string symbol = isUpper ? letter.ToUpperInvariant() : letter.ToLowerInvariant();
        ushort letterVk = (ushort)(char.ToUpperInvariant(letter[0]));
        var list = new List<CandidateOption>
        {
            new(
                "DeadAcuteLayout",
                $"Tecla morta Agudo (´, Scan 0x1A) + '{symbol}' com pausa 15ms",
                $"Passo 1: Tecla morta agudo (Scan 0x1A) com pausa 15ms -> Passo 2: Tecla '{symbol}' (Shift={isUpper}).",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { ScanCode = 0x1A, DelayMs = 15, Action = KeySequenceAction.Press, Label = "Agudo (´) pausa 15ms" },
                        new() { VirtualKey = letterVk, Shift = isUpper, Action = KeySequenceAction.Press, Label = $"Letra '{symbol}'" }
                    }
                }),

            new(
                "UsIntlApostrophe",
                $"US-Intl: Apóstrofo (', Scan 0x28) + '{symbol}' com pausa 15ms",
                $"Passo 1: Apóstrofo (Scan 0x28) com pausa 15ms -> Passo 2: Tecla '{symbol}' (Shift={isUpper}).",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { ScanCode = 0x28, DelayMs = 15, Action = KeySequenceAction.Press, Label = "Apóstrofo (') pausa 15ms" },
                        new() { VirtualKey = letterVk, Shift = isUpper, Action = KeySequenceAction.Press, Label = $"Letra '{symbol}'" }
                    }
                })
        };

        if (asciiCode > 0)
        {
            list.Add(new(
                $"AltNumpad{asciiCode}",
                $"Sequência física Alt + {asciiCode} no teclado numérico",
                $"Produz '{symbol}' via código Alt numpad {asciiCode}.",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = BuildAltNumpadSteps(asciiCode, symbol)
                }));
        }

        return list;
    }

    private static IReadOnlyList<CandidateOption> GetTildeCandidates(string letter, bool isUpper, int asciiCode)
    {
        string symbol = isUpper ? letter.ToUpperInvariant() : letter.ToLowerInvariant();
        ushort letterVk = (ushort)(char.ToUpperInvariant(letter[0]));
        var list = new List<CandidateOption>
        {
            new(
                "DeadTildeAbnt2",
                $"Tecla morta Til (~, Scan 0x28 no ABNT2) + '{symbol}' com pausa 15ms",
                $"Passo 1: Tecla morta til (Scan 0x28) com pausa 15ms -> Passo 2: Tecla '{symbol}' (Shift={isUpper}).",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { ScanCode = 0x28, DelayMs = 15, Action = KeySequenceAction.Press, Label = "Til (~) pausa 15ms" },
                        new() { VirtualKey = letterVk, Shift = isUpper, Action = KeySequenceAction.Press, Label = $"Letra '{symbol}'" }
                    }
                }),

            new(
                "UsIntlTilde",
                $"US-Intl: Shift + ~ (Scan 0x29) + '{symbol}' com pausa 15ms",
                $"Passo 1: Shift + ~ (Scan 0x29) com pausa 15ms -> Passo 2: Tecla '{symbol}' (Shift={isUpper}).",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { ScanCode = 0x29, Shift = true, DelayMs = 15, Action = KeySequenceAction.Press, Label = "Shift+~ pausa 15ms" },
                        new() { VirtualKey = letterVk, Shift = isUpper, Action = KeySequenceAction.Press, Label = $"Letra '{symbol}'" }
                    }
                })
        };

        if (asciiCode > 0)
        {
            list.Add(new(
                $"AltNumpad{asciiCode}",
                $"Sequência física Alt + {asciiCode} no teclado numérico",
                $"Produz '{symbol}' via código Alt numpad {asciiCode}.",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = BuildAltNumpadSteps(asciiCode, symbol)
                }));
        }

        return list;
    }

    private static IReadOnlyList<CandidateOption> GetCircumflexCandidates(string letter, bool isUpper, int asciiCode)
    {
        string symbol = isUpper ? letter.ToUpperInvariant() : letter.ToLowerInvariant();
        ushort letterVk = (ushort)(char.ToUpperInvariant(letter[0]));
        var list = new List<CandidateOption>
        {
            new(
                "DeadCircumflexAbnt2",
                $"Tecla morta Circunflexo (Shift + Scan 0x28 no ABNT2) + '{symbol}' com pausa 15ms",
                $"Passo 1: Shift + Scan 0x28 (Circunflexo ^) com pausa 15ms -> Passo 2: Tecla '{symbol}' (Shift={isUpper}).",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { ScanCode = 0x28, Shift = true, DelayMs = 15, Action = KeySequenceAction.Press, Label = "Circunflexo (^) pausa 15ms" },
                        new() { VirtualKey = letterVk, Shift = isUpper, Action = KeySequenceAction.Press, Label = $"Letra '{symbol}'" }
                    }
                }),

            new(
                "UsIntlCircumflex",
                $"US-Intl: Shift + 6 (Scan 0x07) + '{symbol}' com pausa 15ms",
                $"Passo 1: Shift + 6 (Scan 0x07) com pausa 15ms -> Passo 2: Tecla '{symbol}' (Shift={isUpper}).",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { ScanCode = 0x07, Shift = true, DelayMs = 15, Action = KeySequenceAction.Press, Label = "Shift+6 (^) pausa 15ms" },
                        new() { VirtualKey = letterVk, Shift = isUpper, Action = KeySequenceAction.Press, Label = $"Letra '{symbol}'" }
                    }
                })
        };

        if (asciiCode > 0)
        {
            list.Add(new(
                $"AltNumpad{asciiCode}",
                $"Sequência física Alt + {asciiCode} no teclado numérico",
                $"Produz '{symbol}' via código Alt numpad {asciiCode}.",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = BuildAltNumpadSteps(asciiCode, symbol)
                }));
        }

        return list;
    }

    private static IReadOnlyList<CandidateOption> GetGraveCandidates(string letter, bool isUpper, int asciiCode)
    {
        string symbol = isUpper ? letter.ToUpperInvariant() : letter.ToLowerInvariant();
        ushort letterVk = (ushort)(char.ToUpperInvariant(letter[0]));
        var list = new List<CandidateOption>
        {
            new(
                "DeadGraveAbnt2",
                $"Tecla morta Grave (Shift + Scan 0x1A no ABNT2) + '{symbol}' com pausa 15ms",
                $"Passo 1: Shift + Scan 0x1A (Grave `) com pausa 15ms -> Passo 2: Tecla '{symbol}' (Shift={isUpper}).",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { ScanCode = 0x1A, Shift = true, DelayMs = 15, Action = KeySequenceAction.Press, Label = "Grave (`) pausa 15ms" },
                        new() { VirtualKey = letterVk, Shift = isUpper, Action = KeySequenceAction.Press, Label = $"Letra '{symbol}'" }
                    }
                }),

            new(
                "UsIntlGrave",
                $"US-Intl: Tecla Grave (` no Scan 0x29) + '{symbol}' com pausa 15ms",
                $"Passo 1: Scan 0x29 (`) com pausa 15ms -> Passo 2: Tecla '{symbol}' (Shift={isUpper}).",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { ScanCode = 0x29, DelayMs = 15, Action = KeySequenceAction.Press, Label = "Grave (`) pausa 15ms" },
                        new() { VirtualKey = letterVk, Shift = isUpper, Action = KeySequenceAction.Press, Label = $"Letra '{symbol}'" }
                    }
                })
        };

        if (asciiCode > 0)
        {
            list.Add(new(
                $"AltNumpad{asciiCode}",
                $"Sequência física Alt + {asciiCode} no teclado numérico",
                $"Produz '{symbol}' via código Alt numpad {asciiCode}.",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = BuildAltNumpadSteps(asciiCode, symbol)
                }));
        }

        return list;
    }

    private static IReadOnlyList<CandidateOption> GetCedillaCandidates(bool isUpper)
    {
        string symbol = isUpper ? "Ç" : "ç";
        int asciiCode = isUpper ? 128 : 135;
        return new[]
        {
            new CandidateOption(
                "Abnt2Cedilla",
                $"Tecla física Ç ABNT2 (Scan 0x27, Shift={isUpper})",
                "ScanCode=0x27 (tecla física do Ç ao lado do L no ABNT2).",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { ScanCode = 0x27, Shift = isUpper, Action = KeySequenceAction.Press, Label = $"Ç ABNT2 (Shift={isUpper})" }
                    }
                }),

            new CandidateOption(
                "UsIntlCedilla",
                $"US-Intl: Apóstrofo (Scan 0x28) + '{symbol}' com pausa 15ms",
                $"Passo 1: Apóstrofo (Scan 0x28) com pausa 15ms -> Passo 2: Tecla 'c' (Shift={isUpper}). Produz Ç no US-Intl.",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = new List<KeySequenceStep>
                    {
                        new() { ScanCode = 0x28, DelayMs = 15, Action = KeySequenceAction.Press, Label = "Apóstrofo (') pausa 15ms" },
                        new() { VirtualKey = 0x43, Shift = isUpper, Action = KeySequenceAction.Press, Label = $"Tecla C (Shift={isUpper})" }
                    }
                }),

            new CandidateOption(
                $"AltNumpad{asciiCode}",
                $"Sequência física Alt + {asciiCode} no teclado numérico",
                $"Produz '{symbol}' via Alt numpad {asciiCode}.",
                SubstitutionMode.Sequencia,
                new CharacterSubstitution
                {
                    From = symbol,
                    Mode = SubstitutionMode.Sequencia,
                    SequenceSteps = BuildAltNumpadSteps(asciiCode, symbol)
                })
        };
    }

    private static IReadOnlyList<CandidateOption> GetGenericFallbackCandidates(string symbol)
    {
        return new[]
        {
            new CandidateOption(
                "TextDefault",
                $"Envio textual padrão: '{symbol}'",
                "Utiliza o mapeamento padrão do layout ativo do host.",
                SubstitutionMode.Texto,
                new CharacterSubstitution
                {
                    From = symbol,
                    To = symbol,
                    Mode = SubstitutionMode.Texto
                })
        };
    }

    public static List<KeySequenceStep> BuildAltNumpadSteps(int code, string labelTarget)
    {
        string digits = code.ToString();
        var steps = new List<KeySequenceStep>
        {
            // Alt Down
            new() { VirtualKey = Win32.VK_LMENU, ScanCode = 0x38, Action = KeySequenceAction.KeyDown, Label = "Alt Down" }
        };

        foreach (char d in digits)
        {
            ushort numpadVk = (ushort)(Win32.VK_NUMPAD0 + (d - '0'));
            ushort numpadScan = (d - '0') switch
            {
                0 => 0x52,
                1 => 0x4F,
                2 => 0x50,
                3 => 0x51,
                4 => 0x4B,
                5 => 0x4C,
                6 => 0x4D,
                7 => 0x47,
                8 => 0x48,
                9 => 0x49,
                _ => 0
            };

            steps.Add(new KeySequenceStep
            {
                VirtualKey = numpadVk,
                ScanCode = numpadScan,
                Extended = false,
                Action = KeySequenceAction.Press,
                DelayMs = 10,
                Label = $"Numpad {d} (Press)"
            });
        }

        // Alt Up
        steps.Add(new() { VirtualKey = Win32.VK_LMENU, ScanCode = 0x38, Action = KeySequenceAction.KeyUp, Label = $"Alt Up → '{labelTarget}'" });
        return steps;
    }
}
