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
    Task TypeTextAsync(
        string text,
        TypingOptions options,
        IProgress<TypingProgress>? progress,
        PauseToken pauseToken,
        CancellationToken cancellationToken);
}
