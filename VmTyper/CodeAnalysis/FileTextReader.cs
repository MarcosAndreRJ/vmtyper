using System.IO;
using System.Text;

namespace VmTyper.CodeAnalysis;

/// <summary>
/// Leitor de arquivos com detecção precisa de codificação (UTF-8, UTF-16, Windows-1252/ANSI)
/// e preservação de BOM e quebras de linha (§Fase 1.3).
/// </summary>
public static class FileTextReader
{
    static FileTextReader()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch { }
    }

    public sealed record FileReadResult(string Text, string EncodingName, bool HasBom);

    public static FileReadResult ReadFile(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        return ReadBytes(bytes);
    }

    public static FileReadResult ReadBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return new FileReadResult(string.Empty, "UTF-8", false);
        }

        // 1. Verificação de BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            string text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return new FileReadResult(text, "UTF-8-BOM", true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            string text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return new FileReadResult(text, "UTF-16LE", true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            string text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return new FileReadResult(text, "UTF-16BE", true);
        }

        // 2. Tenta UTF-8 estrito
        if (IsValidUtf8(bytes))
        {
            string text = Encoding.UTF8.GetString(bytes);
            return new FileReadResult(text, "UTF-8", false);
        }

        // 3. Fallback para Windows-1252 (ANSI tradicional Delphi)
        try
        {
            var ansi = Encoding.GetEncoding(1252);
            string text = ansi.GetString(bytes);
            return new FileReadResult(text, "Windows-1252", false);
        }
        catch
        {
            string text = Encoding.Latin1.GetString(bytes);
            return new FileReadResult(text, "ISO-8859-1", false);
        }
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        int i = 0;
        int len = bytes.Length;
        while (i < len)
        {
            byte b = bytes[i];
            if (b <= 0x7F)
            {
                i++;
                continue;
            }

            int count;
            if ((b & 0xE0) == 0xC0) count = 2;
            else if ((b & 0xF0) == 0xE0) count = 3;
            else if ((b & 0xF8) == 0xF0) count = 4;
            else return false; // Byte de liderança inválido

            if (i + count > len) return false;

            for (int j = 1; j < count; j++)
            {
                if ((bytes[i + j] & 0xC0) != 0x80) return false; // Byte de continuação inválido
            }

            i += count;
        }

        return true;
    }
}
