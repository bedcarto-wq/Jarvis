using System.Text;

namespace Jarvis.Core.Text;

/// <summary>
/// Упрощённая фонетическая транслитерация кириллицы и латиницы в общий «ключ»,
/// чтобы «хром» ≈ «chrome», «телеграм» ≈ «telegram».
/// </summary>
public static class Transliterator
{
    private static readonly Dictionary<char, string> Cyr = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "h", ['ц'] = "c", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sh", ['ъ'] = "",
        ['ы'] = "i", ['ь'] = "", ['э'] = "e", ['ю'] = "u", ['я'] = "a",
    };

    /// <summary>Латиница→кириллица для генерации произносимых псевдонимов.</summary>
    private static readonly (string Lat, string Cyr)[] LatToCyr =
    [
        ("sch", "щ"), ("chr", "хр"), ("sh", "ш"), ("ch", "ч"), ("zh", "ж"), ("kh", "х"), ("ph", "ф"), ("th", "т"),
        ("oo", "у"), ("ee", "и"), ("ck", "к"), ("qu", "кв"), ("ya", "я"), ("yu", "ю"), ("yo", "ё"),
        ("a", "а"), ("b", "б"), ("c", "к"), ("d", "д"), ("e", "е"), ("f", "ф"), ("g", "г"), ("h", "х"),
        ("i", "и"), ("j", "дж"), ("k", "к"), ("l", "л"), ("m", "м"), ("n", "н"), ("o", "о"), ("p", "п"),
        ("q", "к"), ("r", "р"), ("s", "с"), ("t", "т"), ("u", "у"), ("v", "в"), ("w", "в"), ("x", "кс"),
        ("y", "и"), ("z", "з"),
    ];

    public static string ToLatinKey(string text)
    {
        var n = TextNormalizer.Normalize(text);
        var sb = new StringBuilder(n.Length * 2);
        foreach (var ch in n)
        {
            if (Cyr.TryGetValue(ch, out var s)) sb.Append(s);
            else if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (ch == ' ') sb.Append(' ');
        }
        // Фонетическое упрощение: удвоенные буквы, немые окончания и близкие звуки.
        var key = sb.ToString()
            .Replace("chr", "hr").Replace("kh", "h").Replace("ph", "f").Replace("ck", "k").Replace("qu", "kv").Replace("w", "v")
            .Replace("x", "ks").Replace("y", "i").Replace("j", "dzh").Replace("c", "k")
            .Replace("ou", "o").Replace("au", "o").Replace("ea", "i").Replace("ee", "i").Replace("oo", "u");
        var outSb = new StringBuilder(key.Length);
        foreach (var ch in key)
            if (outSb.Length == 0 || outSb[^1] != ch) outSb.Append(ch);
        var words = outSb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 3 && w.EndsWith('e') ? w[..^1] : w);
        return string.Join(' ', words);
    }

    public static string ToCyrillic(string latin)
    {
        var s = latin.ToLowerInvariant();
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            if (!char.IsAsciiLetter(s[i]))
            {
                sb.Append(char.IsLetterOrDigit(s[i]) ? s[i] : ' ');
                i++;
                continue;
            }
            bool matched = false;
            foreach (var (lat, cyr) in LatToCyr)
            {
                if (string.CompareOrdinal(s, i, lat, 0, lat.Length) == 0)
                {
                    // Немая «e» в конце слова (chrome → хром).
                    if (lat == "e" && (i + 1 == s.Length || !char.IsAsciiLetter(s[i + 1])) && i > 2) { i++; matched = true; break; }
                    sb.Append(cyr);
                    i += lat.Length;
                    matched = true;
                    break;
                }
            }
            if (!matched) i++;
        }
        return TextNormalizer.Normalize(sb.ToString());
    }

    public static bool ContainsCyrillic(string s) => s.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');
}
