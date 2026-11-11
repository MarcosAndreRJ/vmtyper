using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using VmTyper.Models;
using Application = System.Windows.Application;

namespace VmTyper.Services;

/// <summary>
/// Gerenciador centralizado de temas visuais (Claro, Escuro, Automático) da aplicação (§Fase 4).
/// A troca ocorre em tempo de execução, trocando apenas o ResourceDictionary de cores:
/// a janela principal e o estado da sessão nunca são recriados.
/// Também acompanha o tema do Windows quando o modo é <see cref="AppTheme.Auto"/>.
/// </summary>
public sealed class ThemeManager : INotifyPropertyChanged
{
    private static readonly Lazy<ThemeManager> _instance = new(() => new ThemeManager());
    public static ThemeManager Instance => _instance.Value;

    private const string DarkThemeUri = "pack://application:,,,/VmTyper;component/Themes/DarkTheme.xaml";
    private const string LightThemeUri = "pack://application:,,,/VmTyper;component/Themes/LightTheme.xaml";

    private AppTheme _currentTheme = AppTheme.Auto;
    private bool _isDarkEffectively;

    /// <summary>Preferência do usuário (Automático, Claro ou Escuro).</summary>
    public AppTheme CurrentTheme => _currentTheme;

    /// <summary>Tema efetivamente aplicado agora (resolve o modo Automático).</summary>
    public bool IsDarkEffectively => _isDarkEffectively;

    /// <summary>Alias semântico de <see cref="IsDarkEffectively"/>.</summary>
    public bool IsDarkTheme => _isDarkEffectively;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Disparado após cada troca efetiva de dicionário. O argumento indica se o tema agora é escuro.</summary>
    public event EventHandler<bool>? ThemeChanged;

    private ThemeManager()
    {
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch
        {
            // Sem permissão para eventos do sistema: o modo Automático só reavalia ao reaplicar.
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_currentTheme != AppTheme.Auto) return;
        if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new Action(() => ApplyTheme(AppTheme.Auto)));
    }

    /// <summary>Aplica a preferência. Só troca o dicionário se o tema efetivo mudou (ou se <paramref name="force"/>).</summary>
    public void ApplyTheme(AppTheme theme, bool force = false)
    {
        bool preferenceChanged = _currentTheme != theme;
        _currentTheme = theme;
        if (preferenceChanged) Raise(nameof(CurrentTheme));

        bool effectiveDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => DetectWindowsDarkTheme()
        };

        var resources = Application.Current?.Resources;
        bool hasThemeDictionary = resources is not null && FindThemeDictionaryIndex(resources) >= 0;

        if (!force && hasThemeDictionary && effectiveDark == _isDarkEffectively)
        {
            return;
        }

        _isDarkEffectively = effectiveDark;
        Raise(nameof(IsDarkEffectively));
        Raise(nameof(IsDarkTheme));

        try
        {
            if (resources is not null)
            {
                var newDictionary = new ResourceDictionary
                {
                    Source = new Uri(effectiveDark ? DarkThemeUri : LightThemeUri, UriKind.Absolute)
                };

                int index = FindThemeDictionaryIndex(resources);
                if (index >= 0)
                {
                    // Substitui no mesmo lugar para preservar a ordem de precedência dos demais dicionários.
                    resources.MergedDictionaries[index] = newDictionary;
                }
                else
                {
                    resources.MergedDictionaries.Insert(0, newDictionary);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThemeManager] Falha ao alternar tema: {ex.Message}");
        }

        ThemeChanged?.Invoke(this, effectiveDark);
    }

    /// <summary>Alterna entre Automático → Escuro → Claro → Automático e devolve a nova preferência.</summary>
    public AppTheme CycleTheme()
    {
        var next = _currentTheme switch
        {
            AppTheme.Auto => AppTheme.Dark,
            AppTheme.Dark => AppTheme.Light,
            _ => AppTheme.Auto
        };
        ApplyTheme(next);
        return next;
    }

    /// <summary>Alterna entre claro e escuro explícitos.</summary>
    public void ToggleTheme() => ApplyTheme(_isDarkEffectively ? AppTheme.Light : AppTheme.Dark);

    private static int FindThemeDictionaryIndex(ResourceDictionary resources)
    {
        for (int i = 0; i < resources.MergedDictionaries.Count; i++)
        {
            string? source = resources.MergedDictionaries[i].Source?.ToString();
            if (source is not null &&
                (source.Contains("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                 source.Contains("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }
        return -1;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static bool DetectWindowsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int lightTheme)
            {
                return lightTheme == 0;
            }
        }
        catch
        {
            // Falha de leitura do registro: assume claro.
        }
        return false;
    }
}
