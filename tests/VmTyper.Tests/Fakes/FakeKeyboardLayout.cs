using VmTyper.Native;

namespace VmTyper.Tests.Fakes;

/// <summary>
/// Layout de teclado fixo para testes, independente do layout instalado na
/// máquina. Cobre só o necessário: letras, dígitos, espaço, um pouco de
/// pontuação, as teclas mortas do layout e as teclas especiais que o motor
/// usa. Os scan codes são os do conjunto 1 de um teclado físico real.
/// </summary>
internal sealed class FakeKeyboardLayout : IKeyboardLayoutProvider
{
    public const byte ShiftState = 0x01;
    public const byte CtrlState = 0x02;
    public const byte AltState = 0x04;

    public const ushort ScanSpace = 0x39;
    public const ushort ScanLShift = 0x2A;

    private static readonly IntPtr Hkl = (IntPtr)0x04160416;

    private readonly Dictionary<char, (byte vk, byte shiftState)> _chars = new();
    private readonly Dictionary<uint, ushort> _scanByVk = new();
    private readonly Dictionary<uint, char> _charByVk = new();
    private readonly HashSet<uint> _extendedVks = new();

    /// <summary>Teclas mortas deste layout (o caractere que VkKeyScan devolve como tecla morta).</summary>
    public HashSet<char> DeadKeys { get; } = new();
    private readonly HashSet<uint> _deadVks = new();

    public IReadOnlyDictionary<uint, ushort> ScanByVk => _scanByVk;

    private FakeKeyboardLayout()
    {
        const string row1 = "qwertyuiop", row2 = "asdfghjkl", row3 = "zxcvbnm";
        ushort[] row1Scans = { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19 };
        ushort[] row2Scans = { 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26 };
        ushort[] row3Scans = { 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32 };
        AddLetters(row1, row1Scans);
        AddLetters(row2, row2Scans);
        AddLetters(row3, row3Scans);

        for (int d = 1; d <= 9; d++) AddKey((char)('0' + d), (byte)(0x30 + d), (ushort)(0x01 + d));
        AddKey('0', 0x30, 0x0B);
        AddKey(' ', 0x20, ScanSpace);

        // Teclas especiais e modificadores (VK → scan).
        Scan(Win32.VK_RETURN, 0x1C);
        Scan(Win32.VK_TAB, 0x0F);
        Scan(Win32.VK_HOME, 0x47, extended: true);
        Scan(Win32.VK_DELETE, 0x53, extended: true);
        Scan(Win32.VK_LSHIFT, ScanLShift);
        Scan(Win32.VK_RSHIFT, 0x36);
        Scan(Win32.VK_LCONTROL, 0x1D);
        Scan(Win32.VK_RCONTROL, 0x1D, extended: true);
        Scan(Win32.VK_LMENU, 0x38);
        Scan(Win32.VK_RMENU, 0x38, extended: true);
        Scan(0x6F, 0x35, extended: true); // VK_DIVIDE (/ do numpad)
    }

    /// <summary>Subconjunto do ABNT2 (pt-BR): ~ ^ ´ ` ¨ são teclas mortas.</summary>
    public static FakeKeyboardLayout Abnt2()
    {
        var l = new FakeKeyboardLayout();
        l.AddKey('~', 0xDE, 0x28);
        l.AddChar('^', 0xDE, ShiftState);
        l.AddKey('´', 0xDB, 0x1A);
        l.AddChar('`', 0xDB, ShiftState);
        l.AddChar('¨', 0x36, ShiftState);
        l.DeadKeys.UnionWith(new[] { '~', '^', '´', '`', '¨' });
        l._deadVks.UnionWith(new uint[] { 0xDE, 0xDB, 0x36 });
        l.AddKey('ç', 0xBA, 0x27);
        l.AddChar('Ç', 0xBA, ShiftState);
        l.AddKey(',', 0xBC, 0x33);
        l.AddKey('.', 0xBE, 0x34);
        l.AddKey(';', 0xBF, 0x35);
        l.AddChar(':', 0xBF, ShiftState);
        l.AddKey('-', 0xBD, 0x0C);
        l.AddChar('_', 0xBD, ShiftState);
        l.AddKey('=', 0xBB, 0x0D);
        l.AddChar('+', 0xBB, ShiftState);
        l.AddKey('/', 0xC1, 0x73);
        l.AddChar('!', 0x31, ShiftState);
        l.AddChar('@', 0x32, ShiftState);
        l.AddChar('#', 0x33, ShiftState);
        l.AddChar('$', 0x34, ShiftState);
        l.AddChar('%', 0x35, ShiftState);
        l.AddChar('&', 0x37, ShiftState);
        l.AddChar('*', 0x38, ShiftState);
        l.AddChar('(', 0x39, ShiftState);
        l.AddChar(')', 0x30, ShiftState);
        return l;
    }

    /// <summary>Subconjunto do US-International: ' " ~ ^ ` são teclas mortas.</summary>
    public static FakeKeyboardLayout UsInternational()
    {
        var l = new FakeKeyboardLayout();
        l.AddKey('\'', 0xDE, 0x28);
        l.AddChar('"', 0xDE, ShiftState);
        l.AddKey('`', 0xC0, 0x29);
        l.AddChar('~', 0xC0, ShiftState);
        l.AddChar('^', 0x36, ShiftState);
        l.DeadKeys.UnionWith(new[] { '\'', '"', '~', '^', '`' });
        l._deadVks.UnionWith(new uint[] { 0xDE, 0xC0 });
        return l;
    }

    private void AddLetters(string letters, ushort[] scans)
    {
        for (int i = 0; i < letters.Length; i++)
        {
            byte vk = (byte)char.ToUpperInvariant(letters[i]);
            AddKey(letters[i], vk, scans[i]);
            AddChar(char.ToUpperInvariant(letters[i]), vk, ShiftState);
        }
    }

    private void AddKey(char c, byte vk, ushort scan)
    {
        AddChar(c, vk, 0);
        Scan(vk, scan);
    }

    private void AddChar(char c, byte vk, byte shiftState)
    {
        _chars[c] = (vk, shiftState);
        if (shiftState == 0) _charByVk[vk] = c;
    }

    private void Scan(uint vk, ushort scan, bool extended = false)
    {
        _scanByVk[vk] = scan;
        if (extended) _extendedVks.Add(vk);
    }

    public IntPtr GetLayout() => Hkl;

    public short VkKeyScan(char c, IntPtr layout)
    {
        if (!_chars.TryGetValue(c, out var k)) return -1;
        return (short)((k.shiftState << 8) | k.vk);
    }

    public uint MapVirtualKey(uint code, uint mapType, IntPtr layout)
    {
        if (mapType == Win32.MAPVK_VK_TO_CHAR)
        {
            uint ch = _charByVk.TryGetValue(code, out char c) ? c : 0u;
            if (_deadVks.Contains(code)) ch |= 0x80000000u;
            return ch;
        }
        if (!_scanByVk.TryGetValue(code, out var scan)) return 0;
        if (mapType == Win32.MAPVK_VK_TO_VSC_EX && _extendedVks.Contains(code)) return 0xE000u | scan;
        return scan;
    }

    public bool IsExtendedVk(uint vk) => _extendedVks.Contains(vk);

    /// <summary>
    /// Decodifica o fluxo de <see cref="RecordedInput"/> como um receptor Windows
    /// com este layout, incluindo composição de teclas mortas e pares substitutos Unicode.
    /// </summary>
    public string Decode(IReadOnlyList<RecordedInput> events)
    {
        var sb = new System.Text.StringBuilder();
        bool shift = false;
        char? pendingDead = null;

        var charByScanAndShift = new Dictionary<(ushort scan, byte shift), char>();
        foreach (var (ch, (vk, st)) in _chars)
        {
            if (_scanByVk.TryGetValue(vk, out ushort sc))
            {
                charByScanAndShift[(sc, st)] = ch;
            }
        }

        foreach (var e in events)
        {
            if (e.IsUnicode)
            {
                if (!e.IsKeyUp)
                {
                    if (pendingDead is char d)
                    {
                        sb.Append(d);
                        pendingDead = null;
                    }
                    sb.Append((char)e.Scan);
                }
                continue;
            }

            ushort scan = e.IsScanCode ? e.Scan : (_scanByVk.TryGetValue(e.Vk, out var s) ? s : (ushort)0);

            if (scan is ScanLShift or 0x36)
            {
                shift = !e.IsKeyUp;
                continue;
            }

            if (e.IsKeyUp) continue;

            if (scan == 0x1C) // Enter
            {
                if (pendingDead is char d) { sb.Append(d); pendingDead = null; }
                sb.Append('\n');
                continue;
            }
            if (scan == 0x0F) // Tab
            {
                if (pendingDead is char d) { sb.Append(d); pendingDead = null; }
                sb.Append('\t');
                continue;
            }

            byte st = shift ? ShiftState : (byte)0;
            if (!charByScanAndShift.TryGetValue((scan, st), out char ch))
            {
                continue;
            }

            if (pendingDead is char dead)
            {
                pendingDead = null;
                if (ch == ' ')
                {
                    sb.Append(dead);
                }
                else if (TryComposeDeadKey(dead, ch, out char composed))
                {
                    sb.Append(composed);
                }
                else
                {
                    sb.Append(dead);
                    sb.Append(ch);
                }
            }
            else if (DeadKeys.Contains(ch))
            {
                pendingDead = ch;
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (pendingDead is char trailing)
        {
            sb.Append(trailing);
        }

        return sb.ToString();
    }

    private static bool TryComposeDeadKey(char dead, char baseChar, out char composed)
    {
        composed = (dead, baseChar) switch
        {
            ('´', 'a') or ('\'', 'a') => 'á',
            ('´', 'e') or ('\'', 'e') => 'é',
            ('´', 'i') or ('\'', 'i') => 'í',
            ('´', 'o') or ('\'', 'o') => 'ó',
            ('´', 'u') or ('\'', 'u') => 'ú',
            ('\'', 'c') => 'ç',
            ('´', 'A') or ('\'', 'A') => 'Á',
            ('´', 'E') or ('\'', 'E') => 'É',
            ('´', 'I') or ('\'', 'I') => 'Í',
            ('´', 'O') or ('\'', 'O') => 'Ó',
            ('´', 'U') or ('\'', 'U') => 'Ú',
            ('\'', 'C') => 'Ç',
            ('`', 'a') => 'à', ('`', 'e') => 'è', ('`', 'i') => 'ì', ('`', 'o') => 'ò', ('`', 'u') => 'ù',
            ('`', 'A') => 'À', ('`', 'E') => 'È', ('`', 'I') => 'Ì', ('`', 'O') => 'Ò', ('`', 'U') => 'Ù',
            ('~', 'a') => 'ã', ('~', 'o') => 'õ', ('~', 'n') => 'ñ',
            ('~', 'A') => 'Ã', ('~', 'O') => 'Õ', ('~', 'N') => 'Ñ',
            ('^', 'a') => 'â', ('^', 'e') => 'ê', ('^', 'i') => 'î', ('^', 'o') => 'ô', ('^', 'u') => 'û',
            ('^', 'A') => 'Â', ('^', 'E') => 'Ê', ('^', 'I') => 'Î', ('^', 'O') => 'Ô', ('^', 'U') => 'Û',
            ('¨', 'a') or ('"', 'a') => 'ä',
            ('¨', 'e') or ('"', 'e') => 'ë',
            ('¨', 'i') or ('"', 'i') => 'ï',
            ('¨', 'o') or ('"', 'o') => 'ö',
            ('¨', 'u') or ('"', 'u') => 'ü',
            ('¨', 'A') or ('"', 'A') => 'Ä',
            ('¨', 'E') or ('"', 'E') => 'Ë',
            ('¨', 'I') or ('"', 'I') => 'Ï',
            ('¨', 'O') or ('"', 'O') => 'Ö',
            ('¨', 'U') or ('"', 'U') => 'Ü',
            _ => '\0'
        };
        return composed != '\0';
    }
}
