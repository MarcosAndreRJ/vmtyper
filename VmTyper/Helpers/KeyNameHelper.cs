namespace VmTyper.Helpers;

/// <summary>
/// Converte códigos de Virtual Key em nomes exibíveis para a UI de
/// configurações de atalhos. Cobre apenas as teclas relevantes para os
/// atalhos configuráveis deste aplicativo (não é um mapeamento exaustivo).
/// </summary>
public static class KeyNameHelper
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x0D] = "Enter",
        [0x09] = "Tab",
        [0x1B] = "Esc",
        [0x20] = "Espaço",
        [0x70] = "F1",
        [0x71] = "F2",
        [0x72] = "F3",
        [0x73] = "F4",
        [0x74] = "F5",
        [0x75] = "F6",
        [0x76] = "F7",
        [0x77] = "F8",
        [0x78] = "F9",
        [0x79] = "F10",
        [0x7A] = "F11",
        [0x7B] = "F12",
    };

    public static string FromVirtualKey(uint vk)
    {
        if (Names.TryGetValue(vk, out var name)) return name;

        // A-Z
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        // 0-9
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();

        return $"VK_{vk:X2}";
    }
}
