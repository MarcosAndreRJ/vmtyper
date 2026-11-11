using VmTyper.Models;

namespace VmTyper.CodeAnalysis;

/// <summary>
/// Analisador estrutural e divisor de código Delphi / Object Pascal (§Fase 3).
/// Invariante inviolável: a partição é completa e contígua.
/// string.Concat(blocks.Select(b => b.GetText(text))) == text.
/// </summary>
public static class DelphiCodeSplitter
{
    public static List<CodeBlock> Split(SourceDocument document, int level)
    {
        return SplitText(document.Id, document.Revision, document.Text, level);
    }

    public static List<CodeBlock> SplitText(Guid docId, int revision, string text, int level)
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
                    Title = "Documento Vazio",
                    Kind = CodeBlockKind.FullDocument,
                    StartLine = 1,
                    EndLine = 1
                }
            };
        }

        // Nível 1: Arquivo Completo
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
                    Title = "Arquivo Completo",
                    Kind = CodeBlockKind.FullDocument,
                    StartLine = 1,
                    EndLine = lines
                }
            };
        }

        var tokens = DelphiLexer.Tokenize(text);
        var lineStarts = ComputeLineStarts(text);

        // Nível 2: Seções Principais (Header, Interface, Implementation, Init/Final, End)
        if (level == 2)
        {
            return SplitMainSections(docId, revision, text, tokens, lineStarts);
        }

        // Nível 3 / 4: Declarações e Métodos Independentes
        return SplitDeclarationsAndMethods(docId, revision, text, tokens, lineStarts);
    }

    private static List<CodeBlock> SplitMainSections(
        Guid docId,
        int revision,
        string text,
        List<DelphiToken> tokens,
        List<int> lineStarts)
    {
        var sectionPoints = new List<(int offset, CodeBlockKind kind, string title)>();

        int interfaceOffset = -1;
        int implOffset = -1;
        int initOffset = -1;
        int finalOffset = -1;
        int endOffset = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == DelphiTokenKind.Keyword)
            {
                string word = t.Text.ToLowerInvariant();
                if (word == "interface" && interfaceOffset < 0)
                {
                    interfaceOffset = t.StartOffset;
                    sectionPoints.Add((t.StartOffset, CodeBlockKind.DelphiInterface, "Interface"));
                }
                else if (word == "implementation" && implOffset < 0)
                {
                    implOffset = t.StartOffset;
                    sectionPoints.Add((t.StartOffset, CodeBlockKind.DelphiImplementation, "Implementation"));
                }
                else if (word == "initialization" && initOffset < 0)
                {
                    initOffset = t.StartOffset;
                    sectionPoints.Add((t.StartOffset, CodeBlockKind.DelphiInitialization, "Initialization"));
                }
                else if (word == "finalization" && finalOffset < 0)
                {
                    finalOffset = t.StartOffset;
                    sectionPoints.Add((t.StartOffset, CodeBlockKind.DelphiFinalization, "Finalization"));
                }
                else if (word == "end" && endOffset < 0)
                {
                    // Verifica se é "end."
                    int nextIdx = i + 1;
                    while (nextIdx < tokens.Count && tokens[nextIdx].Kind == DelphiTokenKind.Whitespace) nextIdx++;
                    if (nextIdx < tokens.Count && tokens[nextIdx].Text == ".")
                    {
                        endOffset = t.StartOffset;
                        sectionPoints.Add((t.StartOffset, CodeBlockKind.DelphiUnitEnd, "Encerramento (end.)"));
                    }
                }
            }
        }

        return BuildContiguousBlocks(docId, revision, text, sectionPoints, lineStarts, "Cabeçalho da Unit");
    }

    private static List<CodeBlock> SplitDeclarationsAndMethods(
        Guid docId,
        int revision,
        string text,
        List<DelphiToken> tokens,
        List<int> lineStarts)
    {
        var boundaries = new List<(int offset, CodeBlockKind kind, string title)>();

        int interfaceIdx = -1;
        int implIdx = -1;
        int initIdx = -1;
        int finalIdx = -1;
        int endIdx = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == DelphiTokenKind.Keyword)
            {
                string w = t.Text.ToLowerInvariant();
                if (w == "interface" && interfaceIdx < 0) interfaceIdx = i;
                else if (w == "implementation" && implIdx < 0) implIdx = i;
                else if (w == "initialization" && initIdx < 0) initIdx = i;
                else if (w == "finalization" && finalIdx < 0) finalIdx = i;
                else if (w == "end" && endIdx < 0)
                {
                    int next = i + 1;
                    while (next < tokens.Count && tokens[next].Kind == DelphiTokenKind.Whitespace) next++;
                    if (next < tokens.Count && tokens[next].Text == ".") endIdx = i;
                }
            }
        }

        // Seção Header (0 até interface)
        if (interfaceIdx >= 0)
        {
            boundaries.Add((tokens[interfaceIdx].StartOffset, CodeBlockKind.DelphiInterface, "Interface"));
        }

        // Dentro da Interface: Uses, Types, Consts, Vars
        if (interfaceIdx >= 0)
        {
            int endSearch = implIdx >= 0 ? implIdx : tokens.Count;
            for (int i = interfaceIdx + 1; i < endSearch; i++)
            {
                var t = tokens[i];
                if (t.Kind == DelphiTokenKind.Keyword)
                {
                    string w = t.Text.ToLowerInvariant();
                    if (w == "uses")
                    {
                        boundaries.Add((t.StartOffset, CodeBlockKind.DelphiUses, "Interface: Uses"));
                    }
                    else if (w == "type")
                    {
                        boundaries.Add((t.StartOffset, CodeBlockKind.DelphiTypeSection, "Interface: Tipos"));
                    }
                    else if (w == "const" || w == "resourcestring")
                    {
                        boundaries.Add((t.StartOffset, CodeBlockKind.DelphiConstSection, "Interface: Constantes"));
                    }
                    else if (w == "var" || w == "threadvar")
                    {
                        boundaries.Add((t.StartOffset, CodeBlockKind.DelphiVarSection, "Interface: Variáveis"));
                    }
                }
            }
        }

        // Seção Implementation
        if (implIdx >= 0)
        {
            boundaries.Add((tokens[implIdx].StartOffset, CodeBlockKind.DelphiImplementation, "Implementation"));

            int endImplSearch = initIdx >= 0 ? initIdx : (finalIdx >= 0 ? finalIdx : (endIdx >= 0 ? endIdx : tokens.Count));

            for (int i = implIdx + 1; i < endImplSearch; i++)
            {
                var t = tokens[i];
                if (t.Kind == DelphiTokenKind.Keyword)
                {
                    string w = t.Text.ToLowerInvariant();

                    if (w == "uses")
                    {
                        boundaries.Add((t.StartOffset, CodeBlockKind.DelphiUses, "Implementation: Uses"));
                    }
                    else if (w is "procedure" or "function" or "constructor" or "destructor" or "operator")
                    {
                        // Identifica nome do método (ex.: TPedido.CalcularTotal)
                        string methodName = ExtractMethodTitle(tokens, i);
                        boundaries.Add((t.StartOffset, CodeBlockKind.DelphiMethod, methodName));

                        // Salta o corpo do método analisando begin..end / try..end / case..end
                        i = SkipMethodBody(tokens, i, endImplSearch);
                    }
                }
            }
        }

        // Initialization
        if (initIdx >= 0)
        {
            boundaries.Add((tokens[initIdx].StartOffset, CodeBlockKind.DelphiInitialization, "Initialization"));
        }

        // Finalization
        if (finalIdx >= 0)
        {
            boundaries.Add((tokens[finalIdx].StartOffset, CodeBlockKind.DelphiFinalization, "Finalization"));
        }

        // End.
        if (endIdx >= 0)
        {
            boundaries.Add((tokens[endIdx].StartOffset, CodeBlockKind.DelphiUnitEnd, "Encerramento (end.)"));
        }

        return BuildContiguousBlocks(docId, revision, text, boundaries, lineStarts, "Cabeçalho da Unit");
    }

    private static string ExtractMethodTitle(List<DelphiToken> tokens, int keywordIdx)
    {
        string kind = tokens[keywordIdx].Text; // procedure, function, constructor, etc.
        var nameParts = new List<string>();

        int idx = keywordIdx + 1;
        while (idx < tokens.Count)
        {
            var t = tokens[idx];
            if (t.Kind == DelphiTokenKind.Whitespace || t.Kind == DelphiTokenKind.Comment || t.Kind == DelphiTokenKind.Directive)
            {
                idx++;
                continue;
            }
            if (t.Kind is DelphiTokenKind.Identifier or DelphiTokenKind.Keyword || t.Text == ".")
            {
                nameParts.Add(t.Text);
                idx++;
                continue;
            }
            break; // Parênteses, dois pontos ou ponto e vírgula
        }

        string fullName = string.Join("", nameParts);
        return string.IsNullOrWhiteSpace(fullName) ? kind : $"{kind} {fullName}";
    }

    private static int SkipMethodBody(List<DelphiToken> tokens, int keywordIdx, int limitIdx)
    {
        int idx = keywordIdx + 1;
        bool foundBegin = false;
        int blockDepth = 0;

        while (idx < limitIdx)
        {
            var t = tokens[idx];
            if (t.Kind == DelphiTokenKind.Keyword)
            {
                string w = t.Text.ToLowerInvariant();
                if (w is "begin" or "asm" or "case" or "try" or "record")
                {
                    foundBegin = true;
                    blockDepth++;
                }
                else if (w == "end")
                {
                    blockDepth--;
                    if (foundBegin && blockDepth <= 0)
                    {
                        // Encontrou o end correspondente do método.
                        // Verifica se há ponto e vírgula após o end.
                        int next = idx + 1;
                        while (next < limitIdx && tokens[next].Kind == DelphiTokenKind.Whitespace) next++;
                        if (next < limitIdx && tokens[next].Text == ";")
                        {
                            return next;
                        }
                        return idx;
                    }
                }
                else if (!foundBegin && (w is "forward" or "external"))
                {
                    // Declaração sem corpo (forward/external)
                    int next = idx + 1;
                    while (next < limitIdx && tokens[next].Kind == DelphiTokenKind.Whitespace) next++;
                    if (next < limitIdx && tokens[next].Text == ";") return next;
                    return idx;
                }
                else if (!foundBegin && (w is "procedure" or "function" or "constructor" or "destructor"))
                {
                    // Encontrou outro método antes de um begin (assinatura com erro ou interface)
                    return idx - 1;
                }
            }
            idx++;
        }

        return idx;
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

        // Remove duplicidades de offset mantendo o mais específico
        var uniquePoints = new List<(int offset, CodeBlockKind kind, string title)>();
        foreach (var p in sorted)
        {
            if (uniquePoints.Count == 0 || uniquePoints[^1].offset != p.offset)
            {
                uniquePoints.Add(p);
            }
        }

        var blocks = new List<CodeBlock>();

        // Se o primeiro ponto não começa em 0, cria o bloco inicial [0, primeiro)
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
                Kind = CodeBlockKind.DelphiUnitHeader,
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

        // Garante que, se text.Length > 0 e blocks estiver vazio, cria bloco único
        if (blocks.Count == 0 && text.Length > 0)
        {
            blocks.Add(new CodeBlock
            {
                DocumentId = docId,
                SourceRevision = revision,
                StartOffset = 0,
                EndOffset = text.Length,
                Title = "Código Fonte",
                Kind = CodeBlockKind.Unclassified,
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
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts;
    }

    private static int GetLine(List<int> lineStarts, int offset)
    {
        if (lineStarts.Count == 0) return 1;
        int idx = lineStarts.BinarySearch(offset);
        if (idx >= 0) return idx + 1;
        int complement = ~idx;
        return complement; // 1-based line number
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

