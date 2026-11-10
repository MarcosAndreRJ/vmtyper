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
    private static readonly Dictionary<SourceLanguage, IHighlightingDefinition?> Cache = new();
    private static readonly object Lock = new();

    public static IHighlightingDefinition? GetDefinition(SourceLanguage language)
    {
        lock (Lock)
        {
            if (Cache.TryGetValue(language, out var cached))
            {
                return cached;
            }

            var definition = LoadDefinition(language);
            Cache[language] = definition;
            return definition;
        }
    }

    private static IHighlightingDefinition? LoadDefinition(SourceLanguage language)
    {
        string? resourceName = language switch
        {
            SourceLanguage.Delphi => "VmTyper.Resources.Syntax.Delphi.xshd",
            SourceLanguage.SqlFirebird => "VmTyper.Resources.Syntax.FirebirdSql.xshd",
            _ => null
        };

        if (resourceName == null) return null;

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
