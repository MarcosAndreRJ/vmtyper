using VmTyper.Models;

namespace VmTyper.CodeAnalysis;

/// <summary>
/// Fachada unificada para divisão automática e manual de código (§Fases 3, 4 e 6).
/// Mantém as regras fundamentais de integridade e não-perda de caracteres.
/// </summary>
public static class CodeSplitterService
{
    public static List<CodeBlock> SplitDocument(SourceDocument doc, int? levelOverride = null)
    {
        int level = levelOverride ?? doc.SplitLevel;
        return doc.Language switch
        {
            SourceLanguage.Delphi => DelphiCodeSplitter.Split(doc, level),
            SourceLanguage.SqlFirebird => FirebirdSqlSplitter.Split(doc, level),
            _ => SplitPlainText(doc, level)
        };
    }

    public static List<CodeBlock> SplitPlainText(SourceDocument doc, int level)
    {
        string text = doc.Text;
        if (string.IsNullOrEmpty(text))
        {
            return new List<CodeBlock>
            {
                new()
                {
                    DocumentId = doc.Id,
                    SourceRevision = doc.Revision,
                    StartOffset = 0,
                    EndOffset = 0,
                    Title = "Documento Vazio",
                    Kind = CodeBlockKind.FullDocument,
                    StartLine = 1,
                    EndLine = 1
                }
            };
        }

        // Se level 1, bloco único
        if (level <= 1)
        {
            return new List<CodeBlock>
            {
                new()
                {
                    DocumentId = doc.Id,
                    SourceRevision = doc.Revision,
                    StartOffset = 0,
                    EndOffset = text.Length,
                    Title = "Texto Completo",
                    Kind = CodeBlockKind.FullDocument,
                    StartLine = 1,
                    EndLine = doc.LineCount
                }
            };
        }

        // Se level >= 2, divide por quebras de linha duplas (parágrafos) preservando todo o texto
        var blocks = new List<CodeBlock>();
        int start = 0;
        int blockNum = 1;

        while (start < text.Length)
        {
            int nextBreak = text.IndexOf("\n\n", start, StringComparison.Ordinal);
            if (nextBreak < 0)
            {
                nextBreak = text.Length;
            }
            else
            {
                nextBreak += 2; // Inclui a quebra no bloco
            }

            blocks.Add(new CodeBlock
            {
                DocumentId = doc.Id,
                SourceRevision = doc.Revision,
                StartOffset = start,
                EndOffset = nextBreak,
                Title = $"Parágrafo {blockNum++}",
                Kind = CodeBlockKind.Unclassified
            });

            start = nextBreak;
        }

        return blocks;
    }

    /// <summary>
    /// Divide um bloco existente na posição exata do cursor (§Fase 6).
    /// </summary>
    public static (CodeBlock first, CodeBlock second)? SplitBlockAtOffset(SourceDocument doc, CodeBlock block, int caretOffset)
    {
        if (caretOffset <= block.StartOffset || caretOffset >= block.EndOffset)
        {
            return null;
        }

        var first = new CodeBlock
        {
            DocumentId = doc.Id,
            SourceRevision = doc.Revision,
            StartOffset = block.StartOffset,
            EndOffset = caretOffset,
            Title = $"{block.Title} (Parte 1)",
            Kind = block.Kind,
            Status = block.Status,
            IsEnabled = block.IsEnabled
        };

        var second = new CodeBlock
        {
            DocumentId = doc.Id,
            SourceRevision = doc.Revision,
            StartOffset = caretOffset,
            EndOffset = block.EndOffset,
            Title = $"{block.Title} (Parte 2)",
            Kind = block.Kind,
            Status = TextBlockStatus.Ready,
            IsEnabled = block.IsEnabled
        };

        return (first, second);
    }

    /// <summary>
    /// Une dois blocos adjacentes (§Fase 6).
    /// </summary>
    public static CodeBlock? MergeAdjacentBlocks(SourceDocument doc, CodeBlock first, CodeBlock second)
    {
        if (first.EndOffset != second.StartOffset)
        {
            return null; // Não são estritamente adjacentes
        }

        return new CodeBlock
        {
            DocumentId = doc.Id,
            SourceRevision = doc.Revision,
            StartOffset = first.StartOffset,
            EndOffset = second.EndOffset,
            Title = $"{first.Title} + {second.Title}",
            Kind = first.Kind,
            Status = first.Status,
            IsEnabled = first.IsEnabled
        };
    }

    /// <summary>
    /// Cria um bloco derivado de uma seleção avulsa do usuário (§Fases 5.4 e 6).
    /// </summary>
    public static CodeBlock CreateBlockFromSelection(SourceDocument doc, int startOffset, int endOffset, string? title = null)
    {
        int start = Math.Clamp(startOffset, 0, doc.Text.Length);
        int end = Math.Clamp(endOffset, start, doc.Text.Length);

        return new CodeBlock
        {
            DocumentId = doc.Id,
            SourceRevision = doc.Revision,
            StartOffset = start,
            EndOffset = end,
            Title = !string.IsNullOrWhiteSpace(title) ? title : $"Seleção (L{ComputeLineNumber(doc.Text, start)})",
            Kind = CodeBlockKind.CustomSelection,
            Status = TextBlockStatus.Ready,
            IsEnabled = true
        };
    }

    private static int ComputeLineNumber(string text, int offset)
    {
        if (string.IsNullOrEmpty(text) || offset <= 0) return 1;
        int line = 1;
        int limit = Math.Min(offset, text.Length);
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }
}

