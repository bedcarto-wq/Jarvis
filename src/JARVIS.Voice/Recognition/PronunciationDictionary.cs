using System.Text;
using Jarvis.Core.Text;

namespace Jarvis.Voice.Recognition;

/// <summary>
/// Подбирает произношения для слов грамматики: сначала из словаря модели (ru.dic),
/// затем — правилами (RussianG2P). Латиница предварительно транслитерируется.
/// </summary>
public sealed class PronunciationDictionary
{
    private readonly string? _sourceDic;
    private readonly Dictionary<string, List<string>> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingInSource = new(StringComparer.Ordinal);

    public PronunciationDictionary(string? sourceDic) => _sourceDic = sourceDic is not null && File.Exists(sourceDic) ? sourceDic : null;

    public IReadOnlyList<string> UnknownWords => _unknown;
    private readonly List<string> _unknown = [];

    /// <summary>Возвращает пары (слово, фонемы) для всех слов; варианты произношения — как «слово(2)».</summary>
    public IReadOnlyList<(string Word, string Phones)> Resolve(IEnumerable<string> words)
    {
        var list = words.Where(w => w.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var lookupKeys = list.Select(LookupKey).Where(k => !_cache.ContainsKey(k) && !_missingInSource.Contains(k)).ToHashSet(StringComparer.Ordinal);
        if (lookupKeys.Count > 0) ScanSource(lookupKeys);

        _unknown.Clear();
        var result = new List<(string, string)>();
        foreach (var w in list)
        {
            var key = LookupKey(w);
            if (_cache.TryGetValue(key, out var prons) && prons.Count > 0)
            {
                for (var i = 0; i < prons.Count; i++) result.Add((i == 0 ? w : $"{w}({i + 1})", prons[i]));
                continue;
            }
            var g2p = RussianG2P.Convert(key);
            if (g2p is null) { _unknown.Add(w); continue; }
            result.Add((w, g2p));
        }
        return result;
    }

    internal static string LookupKey(string word)
    {
        var w = word.ToLowerInvariant();
        return Transliterator.ContainsCyrillic(w) || !w.Any(char.IsAsciiLetter) ? w : Transliterator.ToCyrillic(w);
    }

    private void ScanSource(HashSet<string> keys)
    {
        if (_sourceDic is null) { foreach (var k in keys) _missingInSource.Add(k); return; }
        // ru.dic содержит формы и с «ё», и с «е»; ищем точное совпадение и вариант с «ё».
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in keys) wanted[k] = k;
        using var reader = new StreamReader(_sourceDic, Encoding.UTF8, false, 1 << 16);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var word = line[..sp];
            var paren = word.IndexOf('(');
            if (paren > 0) word = word[..paren];
            if (!wanted.ContainsKey(word)) continue;
            if (!_cache.TryGetValue(word, out var prons)) _cache[word] = prons = [];
            var phones = line[(sp + 1)..].Trim();
            if (!prons.Contains(phones)) prons.Add(phones);
        }
        foreach (var k in keys) if (!_cache.ContainsKey(k)) _missingInSource.Add(k);
    }

    public static void WriteDic(string path, IEnumerable<(string Word, string Phones)> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        foreach (var (w, p) in entries) sb.Append(w).Append(' ').Append(p).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
