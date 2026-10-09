using Jarvis.Core.Text;

namespace Jarvis.Core.Commands;

public static class WakePhraseDetector
{
    /// <summary>Отделяет фразу активации от команды. Самая длинная фраза проверяется первой.</summary>
    public static bool TryStrip(string text, IEnumerable<string> wakePhrases, out string rest)
    {
        var n = TextNormalizer.Normalize(text);
        foreach (var phrase in wakePhrases.Select(TextNormalizer.Normalize).Where(p => p.Length > 0).OrderByDescending(p => p.Length))
        {
            if (TextNormalizer.StartsWithWords(n, phrase, out rest)) return true;
        }
        rest = n;
        return false;
    }

    public static bool ContainsStopWord(string text, string stopWord)
    {
        var s = TextNormalizer.Normalize(stopWord);
        return s.Length > 0 && TextNormalizer.ContainsWords(TextNormalizer.Normalize(text), s);
    }
}
