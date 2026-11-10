using VmTyper.Helpers;
using VmTyper.Models;

namespace VmTyper.Services;

/// <summary>
/// Abstração do mecanismo de digitação, independente de UI. A implementação
/// concreta é a única parte do sistema que efetivamente envia eventos de
/// teclado via SendInput.
/// </summary>
public interface IKeyboardInputService
{
    PreflightReport Plan(string text, TypingOptions options) =>
        new(Array.Empty<InputUnit>(), 0, 0, 0, Array.Empty<UnsupportedUnitInfo>(), Array.Empty<string>(), Array.Empty<string>());

    Task TypeTextAsync(
        string text,
        TypingOptions options,
        IProgress<TypingProgress>? progress,
        PauseToken pauseToken,
        CancellationToken cancellationToken);

    void FlushDeadKeyState() { }
}
