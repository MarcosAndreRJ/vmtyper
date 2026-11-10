using System.Text.Json;
using System.Text.Json.Serialization;
using VmTyper.Native;

namespace VmTyper.Services;

/// <summary>
/// Estratégia para tratamento de teclas mortas e caracteres acentuados no CDP (§3).
/// </summary>
public enum CdpDeadKeyStrategy
{
    /// <summary>
    /// Envia eventos <c>key: "Dead"</c> com o <c>code</c> físico da tecla morta (sem <c>text</c>)
    /// e, na tecla seguinte (vogal ou espaço), emite o caractere resultante em <c>text</c>.
    /// </summary>
    DeadKeyThenComposed,

    /// <summary>
    /// Omite os eventos intermediários de tecla morta e despacha diretamente o caractere
    /// final composto em <c>text</c> no evento da tecla principal.
    /// </summary>
    ComposedTextDirect
}

/// <summary>
/// Abstração desacoplada do runtime WebView2 para invocação de métodos Chrome DevTools Protocol.
/// </summary>
public interface ICdpDispatcher
{
    Task<string> CallDevToolsProtocolMethodAsync(string methodName, string parametersJson, CancellationToken ct = default);
}

/// <summary>
/// Parâmetros oficiais do comando CDP <c>Input.dispatchKeyEvent</c>
/// (https://chromedevtools.github.io/devtools-protocol/tot/Input/#method-dispatchKeyEvent).
/// </summary>
public sealed record CdpKeyEventPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("modifiers")] int Modifiers,
    [property: JsonPropertyName("windowsVirtualKeyCode")] int WindowsVirtualKeyCode,
    [property: JsonPropertyName("nativeVirtualKeyCode")] int NativeVirtualKeyCode,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("location")] int Location,
    [property: JsonPropertyName("autoRepeat")] bool AutoRepeat = false,
    [property: JsonPropertyName("isKeypad")] bool IsKeypad = false,
    [property: JsonPropertyName("text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Text = null,
    [property: JsonPropertyName("unmodifiedText")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? UnmodifiedText = null);

/// <summary>
/// Mapeia unidades do <see cref="KeyStrokePlanner"/> e pacotes <see cref="Win32.INPUT"/>
/// para eventos CDP <c>Input.dispatchKeyEvent</c> com tabela oficial de Scan Code Set 1
/// para <c>KeyboardEvent.code</c> (W3C UI Events KeyboardEvent code Values).
/// </summary>
public static class CdpKeyEventMapper
{
    public const int ModAlt = 1;
    public const int ModCtrl = 2;
    public const int ModMeta = 4;
    public const int ModShift = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Tabela PS/2 Set 1 Scan Code → W3C <c>KeyboardEvent.code</c>, VK padrão e Location (0=Standard, 1=Left, 2=Right, 3=Numpad).
    /// Inclui as teclas específicas ABNT2 (<c>0x73 IntlRo</c>, <c>0x56 IntlBackslash</c>).
    /// </summary>
    private static readonly Dictionary<(ushort Scan, bool Extended), (string Code, int Vk, int Location, string BaseKey, string ShiftKey)> ScanTable = new()
    {
        // Fileira numérica
        [(0x29, false)] = ("Backquote", 0xC0, 0, "'", "\""),
        [(0x02, false)] = ("Digit1", 0x31, 0, "1", "!"),
        [(0x03, false)] = ("Digit2", 0x32, 0, "2", "@"),
        [(0x04, false)] = ("Digit3", 0x33, 0, "3", "#"),
        [(0x05, false)] = ("Digit4", 0x34, 0, "4", "$"),
        [(0x06, false)] = ("Digit5", 0x35, 0, "5", "%"),
        [(0x07, false)] = ("Digit6", 0x36, 0, "6", "¨"),
        [(0x08, false)] = ("Digit7", 0x37, 0, "7", "&"),
        [(0x09, false)] = ("Digit8", 0x38, 0, "8", "*"),
        [(0x0A, false)] = ("Digit9", 0x39, 0, "9", "("),
        [(0x0B, false)] = ("Digit0", 0x30, 0, "0", ")"),
        [(0x0C, false)] = ("Minus", 0xBD, 0, "-", "_"),
        [(0x0D, false)] = ("Equal", 0xBB, 0, "=", "+"),
        [(0x0E, false)] = ("Backspace", 0x08, 0, "Backspace", "Backspace"),

        // Fileira QWERTY
        [(0x0F, false)] = ("Tab", 0x09, 0, "Tab", "Tab"),
        [(0x10, false)] = ("KeyQ", 0x51, 0, "q", "Q"),
        [(0x11, false)] = ("KeyW", 0x57, 0, "w", "W"),
        [(0x12, false)] = ("KeyE", 0x45, 0, "e", "E"),
        [(0x13, false)] = ("KeyR", 0x52, 0, "r", "R"),
        [(0x14, false)] = ("KeyT", 0x54, 0, "t", "T"),
        [(0x15, false)] = ("KeyY", 0x59, 0, "y", "Y"),
        [(0x16, false)] = ("KeyU", 0x55, 0, "u", "U"),
        [(0x17, false)] = ("KeyI", 0x49, 0, "i", "I"),
        [(0x18, false)] = ("KeyO", 0x4F, 0, "o", "O"),
        [(0x19, false)] = ("KeyP", 0x50, 0, "p", "P"),
        [(0x1A, false)] = ("BracketLeft", 0xDB, 0, "´", "`"),
        [(0x1B, false)] = ("BracketRight", 0xDD, 0, "[", "{"),
        [(0x1C, false)] = ("Enter", 0x0D, 0, "Enter", "Enter"),
        [(0x1C, true)]  = ("NumpadEnter", 0x0D, 3, "Enter", "Enter"),

        // Fileira ASDF
        [(0x1E, false)] = ("KeyA", 0x41, 0, "a", "A"),
        [(0x1F, false)] = ("KeyS", 0x53, 0, "s", "S"),
        [(0x20, false)] = ("KeyD", 0x44, 0, "d", "D"),
        [(0x21, false)] = ("KeyF", 0x46, 0, "f", "F"),
        [(0x22, false)] = ("KeyG", 0x47, 0, "g", "G"),
        [(0x23, false)] = ("KeyH", 0x48, 0, "h", "H"),
        [(0x24, false)] = ("KeyJ", 0x4A, 0, "j", "J"),
        [(0x25, false)] = ("KeyK", 0x4B, 0, "k", "K"),
        [(0x26, false)] = ("KeyL", 0x4C, 0, "l", "L"),
        [(0x27, false)] = ("Semicolon", 0xBA, 0, "ç", "Ç"),
        [(0x28, false)] = ("Quote", 0xDE, 0, "~", "^"),
        [(0x2B, false)] = ("Backslash", 0xDC, 0, "]", "}"),

        // Fileira ZXCV + ABNT2 extras
        [(0x56, false)] = ("IntlBackslash", 0xE2, 0, "\\", "|"),
        [(0x2C, false)] = ("KeyZ", 0x5A, 0, "z", "Z"),
        [(0x2D, false)] = ("KeyX", 0x58, 0, "x", "X"),
        [(0x2E, false)] = ("KeyC", 0x43, 0, "c", "C"),
        [(0x2F, false)] = ("KeyV", 0x56, 0, "v", "V"),
        [(0x30, false)] = ("KeyB", 0x42, 0, "b", "B"),
        [(0x31, false)] = ("KeyN", 0x4E, 0, "n", "N"),
        [(0x32, false)] = ("KeyM", 0x4D, 0, "m", "M"),
        [(0x33, false)] = ("Comma", 0xBC, 0, ",", "<"),
        [(0x34, false)] = ("Period", 0xBE, 0, ".", ">"),
        [(0x35, false)] = ("Slash", 0xBF, 0, ";", ":"),
        [(0x35, true)]  = ("NumpadDivide", 0x6F, 3, "/", "/"),
        [(0x73, false)] = ("IntlRo", 0xC1, 0, "/", "?"),

        // Modificadores e navegação
        [(0x2A, false)] = ("ShiftLeft", 0xA0, 1, "Shift", "Shift"),
        [(0x36, false)] = ("ShiftRight", 0xA1, 2, "Shift", "Shift"),
        [(0x1D, false)] = ("ControlLeft", 0xA2, 1, "Control", "Control"),
        [(0x1D, true)]  = ("ControlRight", 0xA3, 2, "Control", "Control"),
        [(0x38, false)] = ("AltLeft", 0xA4, 1, "Alt", "Alt"),
        [(0x38, true)]  = ("AltRight", 0xA5, 2, "Alt", "Alt"),
        [(0x39, false)] = ("Space", 0x20, 0, " ", " "),
        [(0x47, true)]  = ("Home", 0x24, 0, "Home", "Home"),
        [(0x4F, true)]  = ("End", 0x23, 0, "End", "End"),
        [(0x53, true)]  = ("Delete", 0x2E, 0, "Delete", "Delete")
    };

    private static readonly HashSet<(ushort Scan, bool Extended)> Abnt2DeadKeyScans = new()
    {
        (0x1A, false), // ´ `
        (0x28, false)  // ~ ^
    };

    public static string Serialize(CdpKeyEventPayload payload) => JsonSerializer.Serialize(payload, JsonOptions);

    /// <summary>
    /// Converte uma <see cref="InputUnit"/> planejada em uma sequência de payloads CDP
    /// segundo a estratégia de tecla morta escolhida (§3).
    /// </summary>
    public static IReadOnlyList<CdpKeyEventPayload> MapUnit(
        InputUnit unit,
        CdpDeadKeyStrategy deadKeyStrategy = CdpDeadKeyStrategy.DeadKeyThenComposed)
    {
        var list = new List<CdpKeyEventPayload>();
        if (unit.Kind == UnitKind.Unsupported || unit.Events.Count == 0)
        {
            return list;
        }

        // Caso especial: estratégia ComposedTextDirect para unidades físicas multi-combo (tecla morta + base):
        int mainDownCount = unit.Events.Count(e => e.Kind == KeyEventKind.Down && !IsModifierScan(e.Code));
        if (deadKeyStrategy == CdpDeadKeyStrategy.ComposedTextDirect &&
            unit.Kind == UnitKind.Physical &&
            mainDownCount > 1)
        {
            // Usa o code/vk da última tecla principal (vogal base ou espaço) mas emite o texto composto diretamente.
            var lastMainDown = unit.Events.Last(e => e.Kind == KeyEventKind.Down && !IsModifierScan(e.Code));
            var info = LookupScan(lastMainDown.Code, lastMainDown.Extended);
            list.Add(new CdpKeyEventPayload(
                Type: "keyDown",
                Modifiers: 0,
                WindowsVirtualKeyCode: info.Vk,
                NativeVirtualKeyCode: info.Vk,
                Key: unit.Text,
                Code: info.Code,
                Location: info.Location,
                IsKeypad: info.Location == 3,
                Text: unit.Text,
                UnmodifiedText: unit.Text));
            list.Add(new CdpKeyEventPayload(
                Type: "keyUp",
                Modifiers: 0,
                WindowsVirtualKeyCode: info.Vk,
                NativeVirtualKeyCode: info.Vk,
                Key: unit.Text,
                Code: info.Code,
                Location: info.Location,
                IsKeypad: info.Location == 3));
            return list;
        }

        int currentModifiers = 0;
        int mainDownIndex = 0;

        foreach (var ev in unit.Events)
        {
            if (ev.Enc == KeyEncoding.Unicode)
            {
                string ch = ((char)ev.Code).ToString();
                if (ev.Kind == KeyEventKind.Down)
                {
                    list.Add(new CdpKeyEventPayload(
                        Type: "keyDown",
                        Modifiers: 0,
                        WindowsVirtualKeyCode: ev.Code,
                        NativeVirtualKeyCode: ev.Code,
                        Key: ch,
                        Code: "",
                        Location: 0,
                        Text: ch,
                        UnmodifiedText: ch));
                }
                else
                {
                    list.Add(new CdpKeyEventPayload(
                        Type: "keyUp",
                        Modifiers: 0,
                        WindowsVirtualKeyCode: ev.Code,
                        NativeVirtualKeyCode: ev.Code,
                        Key: ch,
                        Code: "",
                        Location: 0));
                }
                continue;
            }

            if (TryGetModifierBit(ev.Code, ev.Extended, out int modBit))
            {
                var modInfo = LookupScan(ev.Code, ev.Extended);
                if (ev.Kind == KeyEventKind.Down)
                {
                    currentModifiers |= modBit;
                    list.Add(new CdpKeyEventPayload(
                        Type: "rawKeyDown",
                        Modifiers: currentModifiers,
                        WindowsVirtualKeyCode: modInfo.Vk,
                        NativeVirtualKeyCode: modInfo.Vk,
                        Key: modInfo.BaseKey,
                        Code: modInfo.Code,
                        Location: modInfo.Location));
                }
                else
                {
                    currentModifiers &= ~modBit;
                    list.Add(new CdpKeyEventPayload(
                        Type: "keyUp",
                        Modifiers: currentModifiers,
                        WindowsVirtualKeyCode: modInfo.Vk,
                        NativeVirtualKeyCode: modInfo.Vk,
                        Key: modInfo.BaseKey,
                        Code: modInfo.Code,
                        Location: modInfo.Location));
                }
                continue;
            }

            var scanInfo = LookupScan(ev.Code, ev.Extended);
            bool shiftHeld = (currentModifiers & ModShift) != 0;
            bool isDeadStep = mainDownCount > 1 && mainDownIndex == 0 && IsDeadKeyInAbnt2(ev.Code, ev.Extended, shiftHeld);

            if (ev.Kind == KeyEventKind.Down)
            {
                mainDownIndex++;
                if (isDeadStep)
                {
                    list.Add(new CdpKeyEventPayload(
                        Type: "rawKeyDown",
                        Modifiers: currentModifiers,
                        WindowsVirtualKeyCode: scanInfo.Vk,
                        NativeVirtualKeyCode: scanInfo.Vk,
                        Key: "Dead",
                        Code: scanInfo.Code,
                        Location: scanInfo.Location));
                }
                else
                {
                    bool isLastMainDown = mainDownIndex == mainDownCount;
                    string keyStr = isLastMainDown && IsPrintableUnit(unit)
                        ? unit.Text
                        : (shiftHeld ? scanInfo.ShiftKey : scanInfo.BaseKey);

                    string? textToEmit = unit.Kind switch
                    {
                        UnitKind.Enter => "\r",
                        UnitKind.Tab => "\t",
                        UnitKind.Combo => null,
                        _ => isLastMainDown && IsPrintableUnit(unit) ? unit.Text : null
                    };

                    list.Add(new CdpKeyEventPayload(
                        Type: textToEmit is not null ? "keyDown" : "rawKeyDown",
                        Modifiers: currentModifiers,
                        WindowsVirtualKeyCode: scanInfo.Vk,
                        NativeVirtualKeyCode: scanInfo.Vk,
                        Key: keyStr,
                        Code: scanInfo.Code,
                        Location: scanInfo.Location,
                        IsKeypad: scanInfo.Location == 3,
                        Text: textToEmit,
                        UnmodifiedText: textToEmit));
                }
            }
            else
            {
                string keyStr = isDeadStep
                    ? "Dead"
                    : (IsPrintableUnit(unit) ? unit.Text : (shiftHeld ? scanInfo.ShiftKey : scanInfo.BaseKey));

                list.Add(new CdpKeyEventPayload(
                    Type: "keyUp",
                    Modifiers: currentModifiers,
                    WindowsVirtualKeyCode: scanInfo.Vk,
                    NativeVirtualKeyCode: scanInfo.Vk,
                    Key: keyStr,
                    Code: scanInfo.Code,
                    Location: scanInfo.Location,
                    IsKeypad: scanInfo.Location == 3));
            }
        }

        return list;
    }

    public static (string Code, int Vk, int Location, string BaseKey, string ShiftKey) LookupScan(ushort scan, bool extended)
    {
        if (ScanTable.TryGetValue((scan, extended), out var found))
        {
            return found;
        }
        if (ScanTable.TryGetValue((scan, false), out var fallback))
        {
            return fallback;
        }
        return ($"Scan0x{scan:X2}", 0, 0, "", "");
    }

    public static bool IsModifierScan(ushort scan) => scan is 0x2A or 0x36 or 0x1D or 0x38;

    private static bool TryGetModifierBit(ushort scan, bool extended, out int modBit)
    {
        switch (scan)
        {
            case 0x2A:
            case 0x36:
                modBit = ModShift;
                return true;
            case 0x1D:
                modBit = ModCtrl;
                return true;
            case 0x38:
                modBit = ModAlt;
                return true;
            default:
                modBit = 0;
                return false;
        }
    }

    private static bool IsDeadKeyInAbnt2(ushort scan, bool extended, bool shift)
    {
        if (Abnt2DeadKeyScans.Contains((scan, extended))) return true;
        // '¨' no ABNT2 fica em Shift + '6' (scan 0x07)
        if (scan == 0x07 && !extended && shift) return true;
        return false;
    }

    private static bool IsPrintableUnit(InputUnit unit) =>
        unit.Kind is UnitKind.Physical or UnitKind.Unicode &&
        !string.IsNullOrEmpty(unit.Text) &&
        unit.Text != "\n" &&
        unit.Text != "\t";
}

/// <summary>
/// Implementação de <see cref="IInputInjector"/> que traduz pacotes <see cref="Win32.INPUT"/>
/// em chamadas CDP <c>Input.dispatchKeyEvent</c> para compartilhar <see cref="TypingScheduler"/>,
/// <see cref="TypingTimingPolicy"/>, <see cref="TypingMetrics"/> e <see cref="TypingDiagnosticsEngine"/> (§3).
/// </summary>
internal sealed class CdpInputInjector : IInputInjector
{
    private readonly ICdpDispatcher _dispatcher;
    private readonly CdpDeadKeyStrategy _deadKeyStrategy;
    private int _currentModifiers;
    private char? _pendingDeadChar;

    public int LastError { get; private set; }
    public int DispatchedCount { get; private set; }

    public CdpInputInjector(ICdpDispatcher dispatcher, CdpDeadKeyStrategy deadKeyStrategy = CdpDeadKeyStrategy.DeadKeyThenComposed)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _deadKeyStrategy = deadKeyStrategy;
    }

    public uint Send(ReadOnlySpan<Win32.INPUT> inputs)
    {
        LastError = 0;
        uint sent = 0;

        for (int i = 0; i < inputs.Length; i++)
        {
            var input = inputs[i].U.ki;
            bool isKeyUp = (input.dwFlags & Win32.KEYEVENTF_KEYUP) != 0;
            bool isUnicode = (input.dwFlags & Win32.KEYEVENTF_UNICODE) != 0;
            bool isExtended = (input.dwFlags & Win32.KEYEVENTF_EXTENDEDKEY) != 0;

            var payload = BuildPayloadFromNative(input.wVk, input.wScan, isKeyUp, isUnicode, isExtended);
            if (payload is not null)
            {
                try
                {
                    string json = CdpKeyEventMapper.Serialize(payload);
                    _dispatcher.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", json)
                        .GetAwaiter()
                        .GetResult();
                    DispatchedCount++;
                }
                catch
                {
                    LastError = 5; // ERROR_ACCESS_DENIED / falha de canal CDP
                    return sent;
                }
            }
            sent++;
        }

        return sent;
    }

    public short GetAsyncKeyState(int vKey) => 0;

    private CdpKeyEventPayload? BuildPayloadFromNative(ushort vk, ushort scan, bool isKeyUp, bool isUnicode, bool isExtended)
    {
        if (isUnicode)
        {
            string ch = ((char)scan).ToString();
            return new CdpKeyEventPayload(
                Type: isKeyUp ? "keyUp" : "keyDown",
                Modifiers: 0,
                WindowsVirtualKeyCode: scan,
                NativeVirtualKeyCode: scan,
                Key: ch,
                Code: "",
                Location: 0,
                Text: isKeyUp ? null : ch,
                UnmodifiedText: isKeyUp ? null : ch);
        }

        if (CdpKeyEventMapper.IsModifierScan(scan))
        {
            int bit = scan is 0x2A or 0x36
                ? CdpKeyEventMapper.ModShift
                : (scan == 0x1D ? CdpKeyEventMapper.ModCtrl : CdpKeyEventMapper.ModAlt);

            if (isKeyUp) _currentModifiers &= ~bit;
            else _currentModifiers |= bit;

            var modInfo = CdpKeyEventMapper.LookupScan(scan, isExtended);
            return new CdpKeyEventPayload(
                Type: isKeyUp ? "keyUp" : "rawKeyDown",
                Modifiers: _currentModifiers,
                WindowsVirtualKeyCode: modInfo.Vk,
                NativeVirtualKeyCode: modInfo.Vk,
                Key: modInfo.BaseKey,
                Code: modInfo.Code,
                Location: modInfo.Location);
        }

        var info = CdpKeyEventMapper.LookupScan(scan, isExtended);
        bool shift = (_currentModifiers & CdpKeyEventMapper.ModShift) != 0;
        bool altGr = (_currentModifiers & (CdpKeyEventMapper.ModCtrl | CdpKeyEventMapper.ModAlt)) ==
                     (CdpKeyEventMapper.ModCtrl | CdpKeyEventMapper.ModAlt);

        string rawKey = altGr switch
        {
            true when scan == 0x10 => "/",
            true when scan == 0x11 => "?",
            _ => shift ? info.ShiftKey : info.BaseKey
        };

        bool isDeadKey = rawKey is "´" or "`" or "~" or "^" or "¨";
        if (!isKeyUp && isDeadKey)
        {
            _pendingDeadChar = rawKey[0];
            if (_deadKeyStrategy == CdpDeadKeyStrategy.ComposedTextDirect)
            {
                return null;
            }
            return new CdpKeyEventPayload(
                Type: "rawKeyDown",
                Modifiers: _currentModifiers,
                WindowsVirtualKeyCode: info.Vk,
                NativeVirtualKeyCode: info.Vk,
                Key: "Dead",
                Code: info.Code,
                Location: info.Location);
        }

        if (isKeyUp && isDeadKey)
        {
            if (_deadKeyStrategy == CdpDeadKeyStrategy.ComposedTextDirect)
            {
                return null;
            }
            return new CdpKeyEventPayload(
                Type: "keyUp",
                Modifiers: _currentModifiers,
                WindowsVirtualKeyCode: info.Vk,
                NativeVirtualKeyCode: info.Vk,
                Key: "Dead",
                Code: info.Code,
                Location: info.Location);
        }

        if (!isKeyUp)
        {
            string effectiveKey = rawKey;
            string? text = rawKey switch
            {
                "Enter" => "\r",
                "Tab" => "\t",
                "Home" or "End" or "Delete" or "Backspace" => null,
                _ => rawKey
            };

            if (_pendingDeadChar is char dead && text is not null && text.Length == 1)
            {
                _pendingDeadChar = null;
                text = ComposeDead(dead, text[0]);
                effectiveKey = text;
            }

            return new CdpKeyEventPayload(
                Type: text is not null ? "keyDown" : "rawKeyDown",
                Modifiers: altGr && text is not null ? 0 : _currentModifiers,
                WindowsVirtualKeyCode: info.Vk,
                NativeVirtualKeyCode: info.Vk,
                Key: effectiveKey,
                Code: info.Code,
                Location: info.Location,
                IsKeypad: info.Location == 3,
                Text: text,
                UnmodifiedText: text);
        }

        return new CdpKeyEventPayload(
            Type: "keyUp",
            Modifiers: _currentModifiers,
            WindowsVirtualKeyCode: info.Vk,
            NativeVirtualKeyCode: info.Vk,
            Key: rawKey,
            Code: info.Code,
            Location: info.Location,
            IsKeypad: info.Location == 3);
    }

    private static string ComposeDead(char dead, char b)
    {
        if (b == ' ') return dead.ToString();
        char composed = (dead, b) switch
        {
            ('´', 'a') => 'á', ('´', 'e') => 'é', ('´', 'i') => 'í', ('´', 'o') => 'ó', ('´', 'u') => 'ú',
            ('´', 'A') => 'Á', ('´', 'E') => 'É', ('´', 'I') => 'Í', ('´', 'O') => 'Ó', ('´', 'U') => 'Ú',
            ('`', 'a') => 'à', ('`', 'e') => 'è', ('`', 'i') => 'ì', ('`', 'o') => 'ò', ('`', 'u') => 'ù',
            ('`', 'A') => 'À', ('`', 'E') => 'È', ('`', 'I') => 'Ì', ('`', 'O') => 'Ò', ('`', 'U') => 'Ù',
            ('~', 'a') => 'ã', ('~', 'o') => 'õ', ('~', 'n') => 'ñ',
            ('~', 'A') => 'Ã', ('~', 'O') => 'Õ', ('~', 'N') => 'Ñ',
            ('^', 'a') => 'â', ('^', 'e') => 'ê', ('^', 'i') => 'î', ('^', 'o') => 'ô', ('^', 'u') => 'û',
            ('^', 'A') => 'Â', ('^', 'E') => 'Ê', ('^', 'I') => 'Î', ('^', 'O') => 'Ô', ('^', 'U') => 'Û',
            ('¨', 'a') => 'ä', ('¨', 'e') => 'ë', ('¨', 'i') => 'ï', ('¨', 'o') => 'ö', ('¨', 'u') => 'ü',
            ('¨', 'A') => 'Ä', ('¨', 'E') => 'Ë', ('¨', 'I') => 'Ï', ('¨', 'O') => 'Ö', ('¨', 'U') => 'Ü',
            _ => '\0'
        };
        return composed != '\0' ? composed.ToString() : $"{dead}{b}";
    }
}
