namespace VmTyper.CodeAnalysis;

public enum SqlTokenKind
{
    Whitespace,
    Comment,
    StringLiteral,
    Keyword,
    Identifier,
    Parameter,
    Delimiter,
    Symbol,
    Number,
    EndOfFile
}

public sealed record SqlToken(
    SqlTokenKind Kind,
    string Text,
    int StartOffset,
    int EndOffset);

/// <summary>
/// Analisador léxico para Firebird SQL (§Fase 4.2).
/// Reconhece delimitadores dinâmicos (SET TERM ^ ;), strings com escape '',
/// comentários line e block, identificadores e palavras-chave.
/// </summary>
public static class FirebirdSqlLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "insert", "update", "delete", "merge", "from", "where",
        "group", "by", "having", "order", "join", "inner", "left", "right",
        "full", "outer", "cross", "natural", "on", "as", "union", "all",
        "distinct", "first", "skip", "rows", "to", "into", "values", "set",
        "create", "alter", "drop", "recreate", "table", "view", "index",
        "unique", "constraint", "primary", "key", "foreign", "references",
        "check", "default", "procedure", "trigger", "function", "package",
        "body", "sequence", "generator", "domain", "exception", "role",
        "user", "grant", "revoke", "commit", "rollback", "execute", "block",
        "do", "begin", "end", "declare", "variable", "returns", "suspend",
        "exit", "leave", "while", "for", "loop", "if", "then", "else",
        "when", "case", "and", "or", "not", "in", "exists", "between",
        "like", "is", "null", "containing", "starting", "similar", "with",
        "term"
    };

    public static List<SqlToken> Tokenize(string text)
    {
        var tokens = new List<SqlToken>();
        if (string.IsNullOrEmpty(text))
        {
            tokens.Add(new SqlToken(SqlTokenKind.EndOfFile, string.Empty, 0, 0));
            return tokens;
        }

        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            int start = i;
            char c = text[i];

            // 1. Whitespace
            if (char.IsWhiteSpace(c))
            {
                while (i < len && char.IsWhiteSpace(text[i])) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Whitespace, text[start..i], start, i));
                continue;
            }

            // 2. Line comment: --
            if (c == '-' && i + 1 < len && text[i + 1] == '-')
            {
                while (i < len && text[i] != '\r' && text[i] != '\n') i++;
                tokens.Add(new SqlToken(SqlTokenKind.Comment, text[start..i], start, i));
                continue;
            }

            // 3. Block comment: /* ... */
            if (c == '/' && i + 1 < len && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < len && !(text[i] == '*' && text[i + 1] == '/')) i++;
                if (i + 1 < len && text[i] == '*' && text[i + 1] == '/') i += 2;
                else i = len;
                tokens.Add(new SqlToken(SqlTokenKind.Comment, text[start..i], start, i));
                continue;
            }

            // 4. String literal: '...' with '' escape
            if (c == '\'')
            {
                i++;
                while (i < len)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < len && text[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    i++;
                }
                tokens.Add(new SqlToken(SqlTokenKind.StringLiteral, text[start..i], start, i));
                continue;
            }

            // 5. Delimited Identifier: "..."
            if (c == '"')
            {
                i++;
                while (i < len && text[i] != '"') i++;
                if (i < len && text[i] == '"') i++;
                tokens.Add(new SqlToken(SqlTokenKind.Identifier, text[start..i], start, i));
                continue;
            }

            // 6. Parameter: :param
            if (c == ':' && i + 1 < len && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '$')) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Parameter, text[start..i], start, i));
                continue;
            }

            // 7. Identifier / Keyword
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '$')) i++;
                string word = text[start..i];
                var kind = Keywords.Contains(word) ? SqlTokenKind.Keyword : SqlTokenKind.Identifier;
                tokens.Add(new SqlToken(kind, word, start, i));
                continue;
            }

            // 8. Numbers
            if (char.IsDigit(c))
            {
                while (i < len && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Number, text[start..i], start, i));
                continue;
            }

            // 9. Symbols / Delimiters (; ^ / etc.)
            i++;
            tokens.Add(new SqlToken(SqlTokenKind.Symbol, text[start..i], start, i));
        }

        tokens.Add(new SqlToken(SqlTokenKind.EndOfFile, string.Empty, len, len));
        return tokens;
    }
}

