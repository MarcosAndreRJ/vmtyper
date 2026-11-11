using System.Runtime.InteropServices;

namespace VmTyper.Native;

/// <summary>
/// Fronteira mínima sobre SendInput, para que o motor de teclado possa ser
/// testado sem injetar eventos reais na sessão do usuário.
/// </summary>
internal interface IInputInjector
{
    /// <summary>Envia os eventos e devolve quantos foram efetivamente inseridos na fila de entrada (semântica de SendInput).</summary>
    uint Send(ReadOnlySpan<Win32.INPUT> inputs);

    /// <summary>Código de erro Win32 da última chamada a <see cref="Send"/> que não enviou tudo.</summary>
    int LastError { get; }

    /// <summary>Verifica se uma tecla (modificador) está fisicamente pressionada no momento (GetAsyncKeyState &amp; 0x8000).</summary>
    bool IsPhysicalKeyDown(ushort vk) => false;
}

/// <summary>Implementação real: chama SendInput do user32.</summary>
internal sealed class Win32InputInjector : IInputInjector
{
    public static Win32InputInjector Instance { get; } = new();

    public int LastError { get; private set; }

    public uint Send(ReadOnlySpan<Win32.INPUT> inputs)
    {
        if (inputs.IsEmpty) return 0;
        uint sent = Win32.SendInput((uint)inputs.Length, ref MemoryMarshal.GetReference(inputs), Win32.InputSize);
        if (sent != inputs.Length)
        {
            LastError = Marshal.GetLastWin32Error();
        }
        return sent;
    }

    public bool IsPhysicalKeyDown(ushort vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
}
