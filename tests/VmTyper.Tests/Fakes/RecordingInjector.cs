using System.Diagnostics;
using VmTyper.Native;

namespace VmTyper.Tests.Fakes;

/// <summary>Um evento de teclado que o injetor falso aceitou (ou seja, que "chegou" à fila de entrada).</summary>
internal readonly record struct RecordedInput(ushort Vk, ushort Scan, uint Flags, long Timestamp, int Call)
{
    public bool IsKeyUp => (Flags & Win32.KEYEVENTF_KEYUP) != 0;
    public bool IsScanCode => (Flags & Win32.KEYEVENTF_SCANCODE) != 0;
    public bool IsUnicode => (Flags & Win32.KEYEVENTF_UNICODE) != 0;
    public bool IsExtended => (Flags & Win32.KEYEVENTF_EXTENDEDKEY) != 0;
}

/// <summary>
/// Injetor falso: grava cada INPUT aceito, com carimbo de tempo do
/// Stopwatch, e permite simular envio parcial ("aceitar só N eventos na
/// chamada K"), como SendInput faz quando a UIPI bloqueia ou a fila enche.
/// </summary>
internal sealed class RecordingInjector : IInputInjector
{
    private readonly object _lock = new();
    private readonly Dictionary<int, uint> _partialCalls = new();
    private readonly List<RecordedInput> _delivered = new();

    public int Calls { get; private set; }

    public int LastError { get; private set; }

    public IReadOnlyList<RecordedInput> Delivered
    {
        get { lock (_lock) return _delivered.ToList(); }
    }

    /// <summary>Faz a chamada número <paramref name="callNumber"/> (1-based) aceitar apenas <paramref name="accept"/> eventos e reportar erro 5 (acesso negado).</summary>
    public void FailCall(int callNumber, uint accept)
    {
        lock (_lock) _partialCalls[callNumber] = accept;
    }

    public uint Send(ReadOnlySpan<Win32.INPUT> inputs)
    {
        lock (_lock)
        {
            Calls++;
            uint accept = (uint)inputs.Length;
            if (_partialCalls.TryGetValue(Calls, out var limit))
            {
                accept = Math.Min(limit, accept);
            }

            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < accept; i++)
            {
                var ki = inputs[i].U.ki;
                _delivered.Add(new RecordedInput(ki.wVk, ki.wScan, ki.dwFlags, now, Calls));
            }

            if (accept != inputs.Length)
            {
                LastError = 5;
            }
            return accept;
        }
    }
}
