using System.IO;
using System.Text.RegularExpressions;
using VmTyper.Models;

namespace VmTyper.CodeAnalysis;

public enum DetectionConfidence
{
    High,
    Medium,
    Low,
    Uncertain
}

public sealed record LanguageDetectionResult(
    SourceLanguage Language,
    DetectionConfidence Confidence,
    string Reason);

/// <summary>
/// Serviço de detecção de linguagem por extensão de arquivo e análise estrutural de conteúdo (§Fase 2).
/// </summary>
public static class LanguageDetectionService
{
    private static readonly Regex DelphiUnitRegex = new(
        @"\bunit\s+[A-Za-z0-9_.]+\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DelphiInterfaceImplRegex = new(
        @"\binterface\b[\s\S]*?\bimplementation\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DelphiStructureKeywordsRegex = new(
        @"\b(type\s+[A-Za-z0-9_]+\s*=\s*(class|record|interface)|procedure\s+[A-Za-z0-9_.]+|function\s+[A-Za-z0-9_.]+|begin\b[\s\S]*?\bend[.;])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SqlSetTermRegex = new(
        @"\bSET\s+TERM\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SqlDdlRegex = new(
        @"\b(CREATE\s+(TABLE|VIEW|PROCEDURE|TRIGGER|SEQUENCE|GENERATOR|DOMAIN)|ALTER\s+(TABLE|PROCEDURE|TRIGGER)|DROP\s+(TABLE|VIEW|PROCEDURE|TRIGGER)|EXECUTE\s+BLOCK)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SqlDmlRegex = new(
        @"\b(SELECT\s+[\s\S]+?\s+FROM|INSERT\s+INTO|UPDATE\s+[A-Za-z0-9_]+\s+SET|DELETE\s+FROM|MERGE\s+INTO)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static LanguageDetectionResult Detect(string? fileName, string? content)
    {
        // 1. Detecção por extensão (§2.1)
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            switch (ext)
            {
                case ".pas":
                case ".dpr":
                    return new LanguageDetectionResult(SourceLanguage.Delphi, DetectionConfidence.High, $"Extensão de arquivo '{ext}'");

                case ".inc":
                    // .inc pode ser include de Delphi ou SQL/outro; valida por conteúdo se houver forte evidência, senão padrão é Delphi
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        var contentResult = DetectFromContent(content);
                        if (contentResult.Confidence >= DetectionConfidence.Medium && contentResult.Language != SourceLanguage.PlainText)
                        {
                            return new LanguageDetectionResult(contentResult.Language, DetectionConfidence.High, $"Arquivo .inc confirmado como {contentResult.Language} por análise de conteúdo.");
                        }
                    }
                    return new LanguageDetectionResult(SourceLanguage.Delphi, DetectionConfidence.Medium, "Extensão .inc (include Pascal)");

                case ".sql":
                case ".ddl":
                    return new LanguageDetectionResult(SourceLanguage.SqlFirebird, DetectionConfidence.High, $"Extensão de arquivo '{ext}'");

                case ".txt":
                    // Arquivo .txt: se tiver conteúdo claramente Delphi ou SQL, classifica com confiança média
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        var res = DetectFromContent(content);
                        if (res.Confidence >= DetectionConfidence.Medium)
                        {
                            return res;
                        }
                    }
                    return new LanguageDetectionResult(SourceLanguage.PlainText, DetectionConfidence.High, "Arquivo de texto (.txt)");
            }
        }

        // 2. Detecção por conteúdo (§2.2)
        if (string.IsNullOrWhiteSpace(content))
        {
            return new LanguageDetectionResult(SourceLanguage.PlainText, DetectionConfidence.Uncertain, "Texto vazio");
        }

        return DetectFromContent(content);
    }

    public static LanguageDetectionResult DetectFromContent(string content)
    {
        // Remove comentários e strings para evitar falso-positivo (ex.: string Delphi contendo query SQL)
        string stripped = StripCommentsAndStrings(content);

        // Checagem primária Delphi: unit, interface + implementation
        if (DelphiUnitRegex.IsMatch(stripped) || DelphiInterfaceImplRegex.IsMatch(stripped))
        {
            return new LanguageDetectionResult(SourceLanguage.Delphi, DetectionConfidence.High, "Estrutura de unit Delphi encontrada (unit/interface/implementation).");
        }

        // Checagem primária SQL: SET TERM ou DDL explícito
        if (SqlSetTermRegex.IsMatch(stripped) || SqlDdlRegex.IsMatch(stripped))
        {
            return new LanguageDetectionResult(SourceLanguage.SqlFirebird, DetectionConfidence.High, "Comandos DDL / SET TERM SQL Firebird encontrados.");
        }

        // Checagem secundária Delphi: type ... = class/record, procedure/function com begin/end
        int delphiScore = 0;
        if (DelphiStructureKeywordsRegex.IsMatch(stripped)) delphiScore += 3;
        if (Regex.IsMatch(stripped, @"\b(initialization|finalization)\b", RegexOptions.IgnoreCase)) delphiScore += 2;
        if (Regex.IsMatch(stripped, @"\buses\b[\s\S]+?;", RegexOptions.IgnoreCase)) delphiScore += 2;
        if (Regex.IsMatch(stripped, @"\b(var|const|type)\b", RegexOptions.IgnoreCase)) delphiScore += 1;

        // Checagem secundária SQL: SELECT/INSERT/UPDATE/DELETE/MERGE
        int sqlScore = 0;
        if (SqlDmlRegex.IsMatch(stripped)) sqlScore += 3;
        if (Regex.IsMatch(stripped, @"\b(COMMIT|ROLLBACK)\b", RegexOptions.IgnoreCase)) sqlScore += 2;
        if (Regex.IsMatch(stripped, @"\b(INNER|LEFT|RIGHT|OUTER|CROSS)\s+JOIN\b", RegexOptions.IgnoreCase)) sqlScore += 2;
        if (Regex.IsMatch(stripped, @"\bGROUP\s+BY\b|\bORDER\s+BY\b", RegexOptions.IgnoreCase)) sqlScore += 2;

        if (delphiScore >= 3 && delphiScore > sqlScore)
        {
            return new LanguageDetectionResult(SourceLanguage.Delphi, DetectionConfidence.Medium, "Padrões de sintaxe Delphi / Object Pascal identificados.");
        }

        if (sqlScore >= 3 && sqlScore > delphiScore)
        {
            return new LanguageDetectionResult(SourceLanguage.SqlFirebird, DetectionConfidence.Medium, "Instruções SQL identificadas.");
        }

        if (delphiScore > 0 && delphiScore == sqlScore)
        {
            return new LanguageDetectionResult(SourceLanguage.PlainText, DetectionConfidence.Uncertain, "Indicadores mistos entre Delphi e SQL; mantido como texto simples para segurança.");
        }

        return new LanguageDetectionResult(SourceLanguage.PlainText, DetectionConfidence.Low, "Sem características marcantes de Delphi ou SQL; tratado como texto comum.");
    }

    private static string StripCommentsAndStrings(string text)
    {
        // Remove strings com aspas simples ('...')
        var noStrings = Regex.Replace(text, @"'(''|[^'\r\n])*'", " ");
        // Remove comentários de linha (// ou --)
        var noLineComments = Regex.Replace(noStrings, @"(//|--)[^\r\n]*", " ");
        // Remove comentários de bloco ({...}, (*...*), /*...*/)
        var noBlockComments = Regex.Replace(noLineComments, @"\{[\s\S]*?\}|\(\*[\s\S]*?\*\)|/\*[\s\S]*?\*/", " ");
        return noBlockComments;
    }
}
