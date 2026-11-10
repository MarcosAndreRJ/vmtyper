using VmTyper.Models;

namespace VmTyper.CodeAnalysis;

/// <summary>
/// Analisador sintático e divisor inteligente de scripts SQL Firebird (§Fase 4).
/// Suporta comandos DML, DDL, blocos PSQL com BEGIN..END e alteração dinâmica de terminador (SET TERM).
/// Invariante: string.Concat(blocks.Select(b => b.GetText(text))) == text.
/// </summary>
public static class FirebirdSqlSplitter
{
    public static List<CodeBlock> Split(SourceDocument document, int level = 2)
    {
        return SplitText(document.Id, document.Revision, document.Text, level);
    }

    public static List<CodeBlock> SplitText(Guid docId, int revision, string text, int level = 2)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new List<CodeBlock>
            {
                new()
                {
                    DocumentId = docId,
                    SourceRevision = revision,
                    StartOffset = 0,
                    EndOffset = 0,
                    Title = "Script Vazio",
                    Kind = CodeBlockKind.FullDocument,
                    StartLine = 1,
                    EndLine = 1
                }
            };
        }

        // Nível 1: Script Completo
        if (level <= 1)
        {
            int lines = ComputeLineCount(text);
            return new List<CodeBlock>
            {
                new()
                {
                    DocumentId = docId,
                    SourceRevision = revision,
                    StartOffset = 0,
                    EndOffset = text.Length,
                    Title = "Script Completo",
                    Kind = CodeBlockKind.FullDocument,
                    StartLine = 1,
                    EndLine = lines
                }
            };
        }

        var tokens = FirebirdSqlLexer.Tokenize(text);
        var lineStarts = ComputeLineStarts(text);

        var boundaries = new List<(int offset, CodeBlockKind kind, string title)>();
        string currentDelimiter = ";";

        int i = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.EndOfFile) break;

            if (t.Kind == SqlTokenKind.Whitespace || t.Kind == SqlTokenKind.Comment)
            {
                i++;
                continue;
            }

            int stmtStartOffset = t.StartOffset;

            // 1. Diretiva de cliente: SET TERM <newDelim> <oldDelim>
            if (t.Kind == SqlTokenKind.Keyword && string.Equals(t.Text, "SET", StringComparison.OrdinalIgnoreCase))
            {
                int next = GetNextNonTrivia(tokens, i + 1);
                if (next < tokens.Count && tokens[next].Kind == SqlTokenKind.Keyword &&
                    string.Equals(tokens[next].Text, "TERM", StringComparison.OrdinalIgnoreCase))
                {
                    int delimTokenIdx = GetNextNonTrivia(tokens, next + 1);
                    if (delimTokenIdx < tokens.Count && tokens[delimTokenIdx].Kind != SqlTokenKind.EndOfFile)
                    {
                        string newTerm = tokens[delimTokenIdx].Text;
                        boundaries.Add((stmtStartOffset, CodeBlockKind.SqlSetTerm, $"SET TERM {newTerm}"));

                        // Avança até o antigo delimitador ou final da linha
                        i = delimTokenIdx + 1;
                        while (i < tokens.Count)
                        {
                            var cur = tokens[i];
                            if (cur.Kind == SqlTokenKind.Symbol && cur.Text == currentDelimiter)
                            {
                                i++;
                                break;
                            }
                            if (cur.Text.Contains('\n')) break;
                            i++;
                        }

                        currentDelimiter = newTerm;
                        continue;
                    }
                }
            }

            // 2. Comandos SQL normais ou blocos PSQL
            var (kind, title) = ClassifyStatement(tokens, i);
            boundaries.Add((stmtStartOffset, kind, title));

            // Avança até o delimitador ativo corrente
            i = SkipToDelimiter(tokens, i, currentDelimiter);
        }

        return BuildContiguousBlocks(docId, revision, text, boundaries, lineStarts, "Preâmbulo SQL");
    }

    private static (CodeBlockKind kind, string title) ClassifyStatement(List<SqlToken> tokens, int startIdx)
    {
        var first = tokens[startIdx];
        string word = first.Text.ToUpperInvariant();

        int secondIdx = GetNextNonTrivia(tokens, startIdx + 1);
        string secondWord = secondIdx < tokens.Count ? tokens[secondIdx].Text.ToUpperInvariant() : string.Empty;

        int thirdIdx = secondIdx < tokens.Count ? GetNextNonTrivia(tokens, secondIdx + 1) : tokens.Count;
        string thirdWord = thirdIdx < tokens.Count ? tokens[thirdIdx].Text : string.Empty;

        switch (word)
        {
            case "SELECT":
            {
                // Localiza tabela do FROM para enriquecer o título
                string fromTable = FindFromTable(tokens, startIdx);
                string title = !string.IsNullOrEmpty(fromTable) ? $"SELECT FROM {fromTable}" : "Consulta SELECT";
                return (CodeBlockKind.SqlCommand, title);
            }

            case "INSERT":
                return (CodeBlockKind.SqlCommand, $"INSERT INTO {thirdWord}".Trim());

            case "UPDATE":
                return (CodeBlockKind.SqlCommand, $"UPDATE {secondWord}".Trim());

            case "DELETE":
                return (CodeBlockKind.SqlCommand, $"DELETE FROM {thirdWord}".Trim());

            case "MERGE":
                return (CodeBlockKind.SqlCommand, $"MERGE INTO {thirdWord}".Trim());

            case "WITH":
                return (CodeBlockKind.SqlCommand, $"WITH {secondWord}".Trim());

            case "CREATE" or "RECREATE" or "ALTER" or "DROP":
            {
                if (secondWord is "OR" && thirdWord.Equals("ALTER", StringComparison.OrdinalIgnoreCase))
                {
                    int fourthIdx = thirdIdx < tokens.Count ? GetNextNonTrivia(tokens, thirdIdx + 1) : tokens.Count;
                    string fourthWord = fourthIdx < tokens.Count ? tokens[fourthIdx].Text.ToUpperInvariant() : string.Empty;
                    int fifthIdx = fourthIdx < tokens.Count ? GetNextNonTrivia(tokens, fourthIdx + 1) : tokens.Count;
                    string fifthWord = fifthIdx < tokens.Count ? tokens[fifthIdx].Text : string.Empty;

                    if (fourthWord is "PROCEDURE")
                        return (CodeBlockKind.SqlProcedure, $"CREATE OR ALTER PROCEDURE {fifthWord}".Trim());
                    if (fourthWord is "TRIGGER")
                        return (CodeBlockKind.SqlTrigger, $"CREATE OR ALTER TRIGGER {fifthWord}".Trim());
                    return (CodeBlockKind.SqlDdl, $"CREATE OR ALTER {fourthWord} {fifthWord}".Trim());
                }

                if (secondWord is "PROCEDURE")
                    return (CodeBlockKind.SqlProcedure, $"{word} PROCEDURE {thirdWord}".Trim());
                if (secondWord is "TRIGGER")
                    return (CodeBlockKind.SqlTrigger, $"{word} TRIGGER {thirdWord}".Trim());
                if (secondWord is "TABLE" or "VIEW" or "INDEX" or "SEQUENCE" or "GENERATOR" or "DOMAIN")
                    return (CodeBlockKind.SqlDdl, $"{word} {secondWord} {thirdWord}".Trim());
                return (CodeBlockKind.SqlDdl, $"{word} {secondWord} {thirdWord}".Trim());
            }

            case "EXECUTE":
            {
                if (secondWord == "BLOCK")
                    return (CodeBlockKind.SqlExecuteBlock, "EXECUTE BLOCK");
                if (secondWord == "PROCEDURE")
                    return (CodeBlockKind.SqlCommand, $"EXECUTE PROCEDURE {thirdWord}".Trim());
                return (CodeBlockKind.SqlCommand, $"EXECUTE {secondWord}");
            }

            case "COMMIT":
                return (CodeBlockKind.SqlCommand, "COMMIT");

            case "ROLLBACK":
                return (CodeBlockKind.SqlCommand, "ROLLBACK");

            case "GRANT" or "REVOKE":
                return (CodeBlockKind.SqlDdl, $"{word} {secondWord}");

            default:
                return (CodeBlockKind.SqlCommand, $"Comando {word}");
        }
    }

    private static string FindFromTable(List<SqlToken> tokens, int startIdx)
    {
        for (int i = startIdx; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.Keyword && string.Equals(t.Text, "FROM", StringComparison.OrdinalIgnoreCase))
            {
                int next = GetNextNonTrivia(tokens, i + 1);
                if (next < tokens.Count && (tokens[next].Kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword))
                {
                    return tokens[next].Text;
                }
            }
            if (t.Kind == SqlTokenKind.Symbol && (t.Text == ";" || t.Text == "^")) break;
        }
        return string.Empty;
    }

    private static int SkipToDelimiter(List<SqlToken> tokens, int startIdx, string delimiter)
    {
        int i = startIdx + 1;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.EndOfFile) return i;

            if (t.Kind == SqlTokenKind.Symbol && string.Equals(t.Text, delimiter, StringComparison.Ordinal))
            {
                return i + 1; // Terminou após o delimitador
            }

            i++;
        }
        return i;
    }

    private static int GetNextNonTrivia(List<SqlToken> tokens, int startIdx)
    {
        int i = startIdx;
        while (i < tokens.Count && (tokens[i].Kind == SqlTokenKind.Whitespace || tokens[i].Kind == SqlTokenKind.Comment))
        {
            i++;
        }
        return i;
    }

    private static List<CodeBlock> BuildContiguousBlocks(
        Guid docId,
        int revision,
        string text,
        List<(int offset, CodeBlockKind kind, string title)> rawPoints,
        List<int> lineStarts,
        string defaultInitialTitle)
    {
        var sorted = rawPoints
            .Where(p => p.offset >= 0 && p.offset <= text.Length)
            .OrderBy(p => p.offset)
            .ToList();

        var uniquePoints = new List<(int offset, CodeBlockKind kind, string title)>();
        foreach (var p in sorted)
        {
            if (uniquePoints.Count == 0 || uniquePoints[^1].offset != p.offset)
            {
                uniquePoints.Add(p);
            }
        }

        var blocks = new List<CodeBlock>();

        if (uniquePoints.Count == 0 || uniquePoints[0].offset > 0)
        {
            int end = uniquePoints.Count > 0 ? uniquePoints[0].offset : text.Length;
            blocks.Add(new CodeBlock
            {
                DocumentId = docId,
                SourceRevision = revision,
                StartOffset = 0,
                EndOffset = end,
                Title = defaultInitialTitle,
                Kind = CodeBlockKind.Unclassified,
                StartLine = GetLine(lineStarts, 0),
                EndLine = GetLine(lineStarts, Math.Max(0, end - 1))
            });
        }

        for (int i = 0; i < uniquePoints.Count; i++)
        {
            var current = uniquePoints[i];
            int start = current.offset;
            int end = (i + 1 < uniquePoints.Count) ? uniquePoints[i + 1].offset : text.Length;

            if (end > start)
            {
                blocks.Add(new CodeBlock
                {
                    DocumentId = docId,
                    SourceRevision = revision,
                    StartOffset = start,
                    EndOffset = end,
                    Title = current.title,
                    Kind = current.kind,
                    StartLine = GetLine(lineStarts, start),
                    EndLine = GetLine(lineStarts, Math.Max(start, end - 1))
                });
            }
        }

        if (blocks.Count == 0 && text.Length > 0)
        {
            blocks.Add(new CodeBlock
            {
                DocumentId = docId,
                SourceRevision = revision,
                StartOffset = 0,
                EndOffset = text.Length,
                Title = "Script SQL",
                Kind = CodeBlockKind.SqlCommand,
                StartLine = 1,
                EndLine = lineStarts.Count
            });
        }

        return blocks;
    }

    private static List<int> ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }
        return starts;
    }

    private static int GetLine(List<int> lineStarts, int offset)
    {
        if (lineStarts.Count == 0) return 1;
        int idx = lineStarts.BinarySearch(offset);
        if (idx >= 0) return idx + 1;
        return ~idx;
    }

    private static int ComputeLineCount(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') count++;
        }
        return count;
    }
}
