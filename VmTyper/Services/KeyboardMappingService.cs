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

    public IntPtr GetForegroundLayout()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        uint threadId = Win32.GetWindowThreadProcessId(hwnd, out _);
        return Win32.GetKeyboardLayout(threadId);
    }

    public MappingResult Resolve(char c, IntPtr layout)
    {
        var direct = TryResolveSingle(c, layout);
        if (direct.HasValue)
        {
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

    private static KeyCombo? TryResolveSingle(char c, IntPtr layout)
    {
        short scan = Win32.VkKeyScanEx(c, layout);
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
