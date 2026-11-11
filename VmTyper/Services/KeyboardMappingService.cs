using System.Text;
using VmTyper.Native;

namespace VmTyper.Services;

/// <summary>
/// Combinação de tecla física (Virtual Key) + modificadores necessária para
/// produzir um determinado caractere no layout de teclado ativo.
/// </summary>
public readonly struct KeyCombo
{
    public ushort VirtualKey { get; }
    public bool Shift { get; }
    public bool Ctrl { get; }
    public bool Alt { get; }

    public KeyCombo(ushort virtualKey, bool shift, bool ctrl, bool alt)
    {
        VirtualKey = virtualKey;
        Shift = shift;
        Ctrl = ctrl;
        Alt = alt;
    }
}

public readonly struct MappingResult
{
    public bool Success { get; }
    public IReadOnlyList<KeyCombo> Combos { get; }
    public string? FailureReason { get; }

    private MappingResult(bool success, IReadOnlyList<KeyCombo> combos, string? failureReason)
    {
        Success = success;
        Combos = combos;
        FailureReason = failureReason;
    }

    public static MappingResult Ok(IReadOnlyList<KeyCombo> combos) => new(true, combos, null);
    public static MappingResult Fail(string reason) => new(false, Array.Empty<KeyCombo>(), reason);
}

/// <summary>
/// Resolve caracteres Unicode para sequências de teclas físicas (VK +
/// modificadores), considerando o layout de teclado ativo na janela em
/// primeiro plano (host). Isso é o que torna o "Modo Teclado Físico"
/// compatível com qualquer layout do Windows (ABNT2, US-International etc.)
/// sem precisar de tabelas fixas por idioma.
///
/// Para caracteres acentuados que, no layout ativo, são produzidos por uma
/// TECLA MORTA (dead key) — como é o caso de á/à/ã/â/é/ê/í/ó/ô/õ/ú no
/// layout ABNT2, onde primeiro se pressiona o acento (´ ` ^ ~) e depois a
/// letra — o VkKeyScanEx não retorna um mapeamento direto (retorna -1),
/// pois a API só resolve combinações de UMA tecla física + modificadores.
/// Nesses casos, decompomos o caractere Unicode em sua forma normalizada
/// NFD (letra base + marca de acentuação combinante) e resolvemos cada
/// parte separadamente, reproduzindo a sequência de duas teclas que uma
/// pessoa digitaria fisicamente (tecla morta, depois letra).
/// </summary>
public sealed class KeyboardMappingService
{
    private static readonly Dictionary<char, char> DeadKeyMap = new()
    {
        ['̀'] = '`',  // acento grave
        ['́'] = '´',  // acento agudo
        ['̂'] = '^',  // acento circunflexo
        ['̃'] = '~',  // til
        ['̈'] = '¨',  // trema / diérese
    };

    private static readonly HashSet<char> PotentialDeadLiteralChars = new()
    {
        '~', '^', '´', '`', '¨', '\'', '"'
    };

    private readonly IKeyboardLayoutProvider _layoutProvider;

    internal KeyboardMappingService(IKeyboardLayoutProvider layoutProvider)
    {
        _layoutProvider = layoutProvider;
    }

    public IntPtr GetForegroundLayout() => _layoutProvider.GetLayout();

    public IntPtr GetLayout(string? targetLayout) =>
        string.IsNullOrWhiteSpace(targetLayout) || string.Equals(targetLayout, "HostForeground", StringComparison.OrdinalIgnoreCase)
            ? _layoutProvider.GetLayout()
            : _layoutProvider.LoadLayout(targetLayout);

    /// <summary>
    /// Resolve scan code e flag estendida a partir do VK usando MAPVK_VK_TO_VSC_EX
    /// (prefixo 0xE0/0xE1 → Extended = true) com fallback para <see cref="Win32.IsExtendedKey"/> (§2.1 G).
    /// </summary>
    public (ushort scan, bool extended) ResolveScan(ushort vk, IntPtr layout)
    {
        uint vscEx = _layoutProvider.MapVirtualKey(vk, Win32.MAPVK_VK_TO_VSC_EX, layout);
        if (vscEx == 0)
        {
            vscEx = _layoutProvider.MapVirtualKey(vk, Win32.MAPVK_VK_TO_VSC, layout);
        }

        ushort scan = (ushort)(vscEx & 0xFF);
        bool prefixExtended = (vscEx & 0xFF00) is 0xE000 or 0xE100;
        bool extended = prefixExtended || Win32.IsExtendedKey(vk);
        return (scan, extended);
    }

    /// <summary>
    /// Verifica se o caractere <paramref name="c"/> é produzido por uma tecla morta
    /// literal no layout ativo (N1). Quando verdadeiro, o caractere literal exige
    /// o envio da tecla morta seguida de Espaço.
    /// </summary>
    public bool IsDeadKeyLiteral(char c, byte vk, IntPtr layout)
    {
        if (!PotentialDeadLiteralChars.Contains(c)) return false;

        uint mapChar = _layoutProvider.MapVirtualKey(vk, Win32.MAPVK_VK_TO_CHAR, layout);
        if ((mapChar & 0x80000000u) != 0)
        {
            return true;
        }

        // Alguns layouts (ex.: '^' com Shift+6 no US-Intl ou '¨' com Shift+6 no ABNT2)
        // têm tecla morta apenas no estado com Shift, enquanto MapVirtualKeyEx(MAPVK_VK_TO_CHAR)
        // inspeciona apenas o estado sem modificador. Cobrimos esses casos verificando
        // o KLID / HKL ou a lista de teclas mortas conhecidas do layout.
        if (c is '~' or '^' or '´' or '`' or '¨')
        {
            uint langId = (uint)layout.ToInt64() & 0xFFFF;
            uint deviceId = ((uint)layout.ToInt64() >> 16) & 0xFFFF;
            // pt-BR (0x0416) ou US-International (deviceId 0xF001 ou klid 00020409)
            if (langId == 0x0416 || deviceId == 0xF001 || (langId == 0x0409 && vk == 0x36 && c == '^'))
            {
                return true;
            }
        }

        return false;
    }

    public MappingResult Resolve(char c, IntPtr layout)
    {
        var direct = TryResolveSingle(c, layout);
        if (direct.HasValue)
        {
            if (IsDeadKeyLiteral(c, (byte)direct.Value.VirtualKey, layout))
            {
                return MappingResult.Ok(new[]
                {
                    direct.Value,
                    new KeyCombo(Win32.VK_SPACE, shift: false, ctrl: false, alt: false)
                });
            }
            return MappingResult.Ok(new[] { direct.Value });
        }

        // Tenta decompor em tecla morta + letra base (ex.: 'á' -> '´' + 'a').
        string decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        if (decomposed.Length >= 2)
        {
            char baseChar = decomposed[0];
            var combos = new List<KeyCombo>();
            bool ok = true;

            for (int i = 1; i < decomposed.Length; i++)
            {
                if (!DeadKeyMap.TryGetValue(decomposed[i], out char deadChar))
                {
                    ok = false;
                    break;
                }

                var deadCombo = TryResolveSingle(deadChar, layout);
                if (deadCombo is null && deadChar == '´')
                {
                    // No US-International e layouts semelhantes, o apóstrofo (') atua como tecla morta de acento agudo.
                    deadCombo = TryResolveSingle('\'', layout);
                }
                else if (deadCombo is null && deadChar == '¨')
                {
                    // No US-International, aspas duplas (") atuam como trema/diérese.
                    deadCombo = TryResolveSingle('"', layout);
                }

                if (deadCombo is null)
                {
                    ok = false;
                    break;
                }

                combos.Add(deadCombo.Value);
            }

            if (ok)
            {
                var baseCombo = TryResolveSingle(baseChar, layout);
                if (baseCombo.HasValue)
                {
                    combos.Add(baseCombo.Value);
                    return MappingResult.Ok(combos);
                }
            }
        }

        return MappingResult.Fail(
            $"Caractere '{c}' (U+{(int)c:X4}) não é suportado pelo layout de teclado ativo.");
    }

    private KeyCombo? TryResolveSingle(char c, IntPtr layout)
    {
        short scan = _layoutProvider.VkKeyScan(c, layout);
        if (scan == -1) return null;

        byte vk = (byte)(scan & 0xFF);
        byte shiftState = (byte)((scan >> 8) & 0xFF);
        if (vk == 0xFF) return null;

        bool shift = (shiftState & 0x01) != 0;
        bool ctrl = (shiftState & 0x02) != 0;
        bool alt = (shiftState & 0x04) != 0;

        return new KeyCombo(vk, shift, ctrl, alt);
    }
}
