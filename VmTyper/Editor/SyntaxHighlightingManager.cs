using System.IO;
using System.Reflection;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using VmTyper.Models;

namespace VmTyper.Editor;

/// <summary>
/// Provedor centralizado de definições de realce de sintaxe para o AvalonEdit.
/// </summary>
public static class SyntaxHighlightingManager
{
    private static readonly Dictionary<string, IHighlightingDefinition?> Cache = new();
    private static readonly object Lock = new();

    public static IHighlightingDefinition? GetDefinition(SourceLanguage language, bool? isDark = null)
    {
        bool dark = isDark ?? Services.ThemeManager.Instance.IsDarkTheme;
        string key = $"{language}_{(dark ? "Dark" : "Light")}";

        lock (Lock)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var definition = LoadDefinition(language, dark);
            Cache[key] = definition;
            return definition;
        }
    }

    private static IHighlightingDefinition? LoadDefinition(SourceLanguage language, bool isDark)
    {
        try
        {
            string? resourceName = (language, isDark) switch
            {
                (SourceLanguage.Delphi, true) => "VmTyper.Resources.Syntax.Delphi.Dark.xshd",
                (SourceLanguage.Delphi, false) => "VmTyper.Resources.Syntax.Delphi.xshd",
                (SourceLanguage.SqlFirebird, true) => "VmTyper.Resources.Syntax.FirebirdSql.Dark.xshd",
                (SourceLanguage.SqlFirebird, false) => "VmTyper.Resources.Syntax.FirebirdSql.xshd",
                _ => null
            };

            if (resourceName == null) return null;

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            using var reader = XmlReader.Create(stream);
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyntaxHighlightingManager] Erro ao carregar sintaxe para {language} (Dark={isDark}): {ex.Message}");
            return null;
        }
    }
}

