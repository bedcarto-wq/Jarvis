using System.Text;

namespace Jarvis.Core.Text;

public static class TextNormalizer
{
    /// <summary>Нижний регистр, «ё»→«е», без пунктуации, одиночные пробелы.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (var raw in text.Trim().ToLowerInvariant())
        {
            var ch = raw == 'ё' ? 'е' : raw;
            if (char.IsLetterOrDigit(ch) || ch is '.' or '/' or ':' or '-' or '_')
            {
                if (space && sb.Length > 0) sb.Append(' ');
                sb.Append(ch);
                space = false;
            }
            else
            {
                space = true;
            }
        }
        // Точки и дефисы в конце фразы — пунктуация.
        return sb.ToString().Trim('.', '-', ':', ' ');
    }

    public static string[] Words(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static bool StartsWithWords(string text, string prefix, out string rest)
    {
        rest = text;
        if (prefix.Length == 0) return false;
        if (text == prefix) { rest = ""; return true; }
        if (text.StartsWith(prefix + " ", StringComparison.Ordinal))
        {
            rest = text[(prefix.Length + 1)..].Trim();
            return true;
        }
        return false;
    }

    public static bool ContainsWords(string text, string phrase) =>
        phrase.Length > 0 && (" " + text + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);
}
