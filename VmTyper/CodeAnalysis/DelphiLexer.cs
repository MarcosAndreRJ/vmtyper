namespace VmTyper.CodeAnalysis;

public enum DelphiTokenKind
{
    Whitespace,
    Comment,
    Directive,
    StringLiteral,
    Keyword,
    Identifier,
    Symbol,
    Number,
    EndOfFile
}

public sealed record DelphiToken(
    DelphiTokenKind Kind,
    string Text,
    int StartOffset,
    int EndOffset);

/// <summary>
/// Analisador léxico robusto para Delphi / Object Pascal (§Fase 3.3).
/// Reconhece com precisão comentários //, {}, (**), diretivas {$...}, strings '...',
/// identificadores e palavras reservadas, preservando offsets exatos.
/// </summary>
public static class DelphiLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "unit", "program", "library", "package", "interface", "implementation",
        "initialization", "finalization", "uses", "type", "const", "var",
        "threadvar", "resourcestring", "procedure", "function", "constructor",
        "destructor", "operator", "class", "record", "object", "property",
        "begin", "end", "asm", "try", "finally", "except", "case", "of",
        "if", "then", "else", "for", "to", "downto", "do", "while", "repeat",
        "until", "with", "inherited", "override", "virtual", "abstract",
        "overload", "reintroduce", "inline", "static", "public", "private",
        "protected", "published", "strict", "forward", "external"
    };

    public static List<DelphiToken> Tokenize(string text)
    {
        var tokens = new List<DelphiToken>();
        if (string.IsNullOrEmpty(text))
        {
            tokens.Add(new DelphiToken(DelphiTokenKind.EndOfFile, string.Empty, 0, 0));
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
                tokens.Add(new DelphiToken(DelphiTokenKind.Whitespace, text[start..i], start, i));
                continue;
            }

            // 2. Line comment: //
            if (c == '/' && i + 1 < len && text[i + 1] == '/')
            {
                while (i < len && text[i] != '\r' && text[i] != '\n') i++;
                tokens.Add(new DelphiToken(DelphiTokenKind.Comment, text[start..i], start, i));
                continue;
            }

            // 3. Block comment / directive: { ... }
            if (c == '{')
            {
                bool isDirective = i + 1 < len && text[i + 1] == '$';
                i++;
                while (i < len && text[i] != '}') i++;
                if (i < len && text[i] == '}') i++;
                var kind = isDirective ? DelphiTokenKind.Directive : DelphiTokenKind.Comment;
                tokens.Add(new DelphiToken(kind, text[start..i], start, i));
                continue;
            }

            // 4. Block comment: (* ... *)
            if (c == '(' && i + 1 < len && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < len && !(text[i] == '*' && text[i + 1] == ')')) i++;
                if (i + 1 < len && text[i] == '*' && text[i + 1] == ')') i += 2;
                else i = len;
                tokens.Add(new DelphiToken(DelphiTokenKind.Comment, text[start..i], start, i));
                continue;
            }

            // 5. String literal: '...' with '' escape
            if (c == '\'')
            {
                i++;
                while (i < len)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < len && text[i + 1] == '\'')
                        {
                            i += 2; // escaped quote
                            continue;
                        }
                        i++;
                        break;
                    }
                    if (text[i] == '\r' || text[i] == '\n') break; // unterminated string
                    i++;
                }
                tokens.Add(new DelphiToken(DelphiTokenKind.StringLiteral, text[start..i], start, i));
                continue;
            }

            // 6. Identifier / Keyword
            if (char.IsLetter(c) || c == '_')
            {
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                string word = text[start..i];
                var kind = Keywords.Contains(word) ? DelphiTokenKind.Keyword : DelphiTokenKind.Identifier;
                tokens.Add(new DelphiToken(kind, word, start, i));
                continue;
            }

            // 7. Numbers (decimal or hex $123)
            if (char.IsDigit(c) || (c == '$' && i + 1 < len && "0123456789ABCDEFabcdef".Contains(text[i + 1])))
            {
                if (c == '$') i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '.'))
                {
                    if (text[i] == '.' && i + 1 < len && text[i + 1] == '.') break; // range ..
                    i++;
                }
                tokens.Add(new DelphiToken(DelphiTokenKind.Number, text[start..i], start, i));
                continue;
            }

            // 8. Symbols
            i++;
            tokens.Add(new DelphiToken(DelphiTokenKind.Symbol, text[start..i], start, i));
        }

        tokens.Add(new DelphiToken(DelphiTokenKind.EndOfFile, string.Empty, len, len));
        return tokens;
    }
}

