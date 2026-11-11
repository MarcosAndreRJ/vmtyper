using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using VmTyper.Models;
using VmTyper.Native;

namespace VmTyper.Services;

public sealed record DiagnosticHeaderRecord(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("s")] Guid SessionId,
    [property: JsonPropertyName("startedAtUtc")] DateTime StartedAtUtc,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("layout")] string Layout,
    [property: JsonPropertyName("flags")] FeatureFlags Flags,
    [property: JsonPropertyName("timerResMs")] double TimerResolutionMs,
    [property: JsonPropertyName("os")] string OsDescription,
    [property: JsonPropertyName("kbDelay")] int HostKeyboardDelay,
    [property: JsonPropertyName("kbSpeed")] int HostKeyboardSpeed);

public sealed record DiagnosticEventRecord(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("s")] Guid SessionId,
    [property: JsonPropertyName("i")] int Index,
    [property: JsonPropertyName("cat")] string Category,
    [property: JsonPropertyName("m")] string EncodingMethod,
    [property: JsonPropertyName("vk")] ushort Vk,
    [property: JsonPropertyName("sc")] ushort ScanCode,
    [property: JsonPropertyName("ext")] bool Extended,
    [property: JsonPropertyName("mods")] IReadOnlyList<string> Modifiers,
    [property: JsonPropertyName("tDown")] long TimestampDownUs,
    [property: JsonPropertyName("tUp")] long TimestampUpUs,
    [property: JsonPropertyName("ret")] IReadOnlyList<int> ReturnValues,
    [property: JsonPropertyName("err")] int NativeError,
    [property: JsonPropertyName("gapNext")] double GapNextMs,
    [property: JsonPropertyName("hwnd")] string HwndHex,
    [property: JsonPropertyName("fg")] string ForegroundProcess,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("ch")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Character = null);

/// <summary>
/// Grava telemetria estruturada em JSONL por sessão em <c>%AppData%\VmTyper\diagnostics\session-&lt;id&gt;.jsonl</c>
/// sem armazenar o texto digitado quando <c>DetailedTrace</c> está desligado (Fase 3 §1).
/// </summary>
public sealed class TypingDiagnosticsEngine
{
    private const uint SPI_GETKEYBOARDDELAY = 0x0016;
    private const uint SPI_GETKEYBOARDSPEED = 0x000A;

    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _diagnosticsDirectory;
    private readonly int _maxSessionsToKeep;
    private readonly List<DiagnosticEventRecord> _currentEvents = new();
    private StreamWriter? _writer;
    private Guid _sessionId;
    private long _sessionStartTimestamp;
    private bool _detailedTrace;

    public string DiagnosticsDirectory => _diagnosticsDirectory;
    public string? CurrentFilePath { get; private set; }
    public IReadOnlyList<DiagnosticEventRecord> CurrentEvents => _currentEvents;

    public TypingDiagnosticsEngine(string? diagnosticsDirectory = null, int maxSessionsToKeep = 20)
    {
        _diagnosticsDirectory = diagnosticsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VmTyper",
            "diagnostics");
        _maxSessionsToKeep = Math.Max(1, maxSessionsToKeep);
    }

    public string BeginSession(
        Guid sessionId,
        TypingOptions options,
        TypingProfile profile,
        FeatureFlags flags,
        bool detailedTrace)
    {
        Directory.CreateDirectory(_diagnosticsDirectory);
        RotateOldSessions();

        _sessionId = sessionId;
        _sessionStartTimestamp = Stopwatch.GetTimestamp();
        _detailedTrace = detailedTrace;
        _currentEvents.Clear();

        string fileName = $"session-{sessionId:N}.jsonl";
        CurrentFilePath = Path.Combine(_diagnosticsDirectory, fileName);
        _writer?.Dispose();
        _writer = new StreamWriter(new FileStream(CurrentFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };

        var (kbDelay, kbSpeed) = QueryHostKeyboardSettings();
        var header = new DiagnosticHeaderRecord(
            Type: "header",
            SessionId: sessionId,
            StartedAtUtc: DateTime.UtcNow,
            AppVersion: typeof(TypingDiagnosticsEngine).Assembly.GetName().Version?.ToString() ?? "2.0.0.0",
            Profile: profile.Name,
            Method: options.Method.ToString(),
            Layout: options.TargetLayout,
            Flags: new FeatureFlags
            {
                AdaptiveTiming = flags.AdaptiveTiming,
                TargetGuardStrict = flags.TargetGuardStrict,
                EmbeddedBrowser = flags.EmbeddedBrowser,
                CdpInput = flags.CdpInput,
                DetailedTrace = detailedTrace
            },
            TimerResolutionMs: Math.Round(QueryTimerResolutionMs(), 4),
            OsDescription: RuntimeInformation.OSDescription,
            HostKeyboardDelay: kbDelay,
            HostKeyboardSpeed: kbSpeed);

        _writer.WriteLine(JsonSerializer.Serialize(header, JsonLineOptions));
        return CurrentFilePath;
    }

    public DiagnosticEventRecord RecordEvent(
        InputUnit unit,
        KeyTiming timing,
        UnitResult result,
        IntPtr foregroundHwnd = default,
        string foregroundProcess = "")
    {
        var downEvents = unit.Events.Where(e => e.Kind == KeyEventKind.Down).ToList();
        var mainDown = downEvents.LastOrDefault();
        var mods = new List<string>();
        foreach (var d in downEvents)
        {
            if (d.Enc == KeyEncoding.Scan)
            {
                if (d.Code == 0x2A) mods.Add("LShift");
                else if (d.Code == 0x36) mods.Add("RShift");
                else if (d.Code == 0x1D) mods.Add(d.Extended ? "RCtrl" : "LCtrl");
                else if (d.Code == 0x38) mods.Add(d.Extended ? "RAlt" : "LAlt");
            }
        }

        long tDownUs = result.TimestampDown > 0
            ? (long)((result.TimestampDown - _sessionStartTimestamp) * 1_000_000.0 / Stopwatch.Frequency)
            : 0;
        long tUpUs = result.TimestampUp > 0
            ? (long)((result.TimestampUp - _sessionStartTimestamp) * 1_000_000.0 / Stopwatch.Frequency)
            : tDownUs;

        var retList = new int[unit.Events.Count];
        for (int i = 0; i < unit.Events.Count; i++)
        {
            retList[i] = i < result.EventsSent ? 1 : 0;
        }

        var record = new DiagnosticEventRecord(
            Type: "unit",
            SessionId: _sessionId,
            Index: unit.Index,
            Category: LogService.Categorize(unit.Text),
            EncodingMethod: mainDown.Enc.ToString(),
            Vk: mainDown.Enc == KeyEncoding.Unicode ? mainDown.Code : (ushort)0,
            ScanCode: mainDown.Code,
            Extended: mainDown.Extended,
            Modifiers: mods,
            TimestampDownUs: tDownUs,
            TimestampUpUs: tUpUs,
            ReturnValues: retList,
            NativeError: result.NativeError,
            GapNextMs: Math.Round(timing.GapAfter.TotalMilliseconds, 2),
            HwndHex: $"0x{foregroundHwnd:X}",
            ForegroundProcess: foregroundProcess,
            Note: result.ErrorMessage ?? timing.Reason,
            Character: _detailedTrace ? unit.Text : null);

        _currentEvents.Add(record);
        _writer?.WriteLine(JsonSerializer.Serialize(record, JsonLineOptions));
        return record;
    }

    public void EndSession()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
    }

    public void RotateOldSessions()
    {
        if (!Directory.Exists(_diagnosticsDirectory)) return;

        var files = new DirectoryInfo(_diagnosticsDirectory)
            .GetFiles("session-*.jsonl")
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();

        for (int i = _maxSessionsToKeep; i < files.Count; i++)
        {
            try { files[i].Delete(); } catch { }
        }
    }

    public int PurgeAllDiagnostics()
    {
        EndSession();
        if (!Directory.Exists(_diagnosticsDirectory)) return 0;

        int deleted = 0;
        foreach (var file in Directory.GetFiles(_diagnosticsDirectory, "*.jsonl")
                     .Concat(Directory.GetFiles(_diagnosticsDirectory, "*.log")))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch
            {
                // Ignora arquivo aberto por outro processo.
            }
        }
        return deleted;
    }

    public static IReadOnlyList<DiagnosticEventRecord> LoadEventsFromJsonl(string path)
    {
        var list = new List<DiagnosticEventRecord>();
        if (!File.Exists(path)) return list;

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "unit")
            {
                var ev = JsonSerializer.Deserialize<DiagnosticEventRecord>(line, JsonLineOptions);
                if (ev is not null) list.Add(ev);
            }
        }
        return list;
    }

    public static (int Delay, int Speed) QueryHostKeyboardSettings()
    {
        int delay = 0;
        int speed = 0;
        try
        {
            SystemParametersInfo(SPI_GETKEYBOARDDELAY, 0, ref delay, 0);
            SystemParametersInfo(SPI_GETKEYBOARDSPEED, 0, ref speed, 0);
        }
        catch
        {
            // Ignora falha de leitura.
        }
        return (delay, speed);
    }

    public static double QueryTimerResolutionMs()
    {
        try
        {
            NtQueryTimerResolution(out _, out _, out uint current);
            return current / 10_000.0;
        }
        catch
        {
            return 15.625;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);
}
