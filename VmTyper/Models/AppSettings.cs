namespace VmTyper.Models;

public enum ExpectedBrowser
{
    Nenhum,
    Chrome,
    Edge,
    Firefox
}

/// <summary>
/// Representa um atalho global configurável (modificadores + tecla).
/// </summary>
public sealed class HotkeyDefinition
{
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public uint VirtualKey { get; set; }

    public uint ToModifierFlags()
    {
        uint mods = 0;
        if (Ctrl) mods |= Native.Win32.MOD_CONTROL;
        if (Shift) mods |= Native.Win32.MOD_SHIFT;
        if (Alt) mods |= Native.Win32.MOD_ALT;
        return mods;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        parts.Add(Helpers.KeyNameHelper.FromVirtualKey(VirtualKey));
        return string.Join(" + ", parts);
    }
}

public sealed class AppSettings
{
    // --- Digitação ---
    public int CharDelayMs { get; set; } = 2;
    public int EnterDelayMs { get; set; } = 20;
    public InputMethod Method { get; set; } = InputMethod.Automatico;
    public int CountdownSeconds { get; set; } = 3;

    /// <summary>
    /// Ao iniciar uma nova linha, remove a autoindentação do editor de
    /// destino e reaplica a indentação existente no texto original.
    /// Recomendado para editores de código como Delphi. Desativado por
    /// padrão: exige validação no editor de destino antes de habilitar
    /// (ver README) — não há como confirmar por software que o reset de
    /// cursor funcionou corretamente em todo e qualquer editor.
    /// </summary>
    public bool PreserveOriginalIndentation { get; set; }

    /// <summary>
    /// Tabela de substituição manual de caracteres, para contornar teclas
    /// que a VM de destino digita erradas (layout desconfigurado). Aplicada
    /// ao texto antes do envio, independente do método de digitação.
    /// </summary>
    public List<CharacterSubstitution> CharacterSubstitutions { get; set; } = new();

    // --- Atalhos ---
    public HotkeyDefinition StartHotkey { get; set; } = new() { Ctrl = true, Shift = true, VirtualKey = 0x0D };
    public HotkeyDefinition PauseResumeHotkey { get; set; } = new() { Ctrl = true, Shift = true, VirtualKey = 0x50 };
    // O cancelamento é fixo em ESC por design: é registrado como hotkey
    // global apenas enquanto uma digitação está ativa (contagem, digitando
    // ou pausado), evitando interferir com o uso normal da tecla ESC no
    // resto do sistema quando o app está ocioso.

    // --- Segurança ---
    public bool ValidateTargetWindow { get; set; }
    public ExpectedBrowser ExpectedBrowser { get; set; } = ExpectedBrowser.Chrome;

    // --- Comportamento ---
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool ClearTextAfterCompletion { get; set; }
}
