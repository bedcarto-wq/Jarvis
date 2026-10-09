using System.Text;

namespace Jarvis.Voice.Recognition;

/// <summary>
/// Правиловое преобразование русского слова в фонемы модели CMUSphinx (порт dictionary.py
/// из cmusphinx-ru). Ударение: «ё» или «+» перед гласной; иначе — предпоследний слог.
/// </summary>
public static class RussianG2P
{
    private const string SoftLetters = "яёюиье";
    private const string StartSyl = "#ъьаяоёуюэеиы-";
    private const string Vowels = "аяуюоёэеиы";

    private static readonly Dictionary<char, string> SoftHard = new()
    {
        ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['з'] = "z", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['ф'] = "f", ['х'] = "h",
    };

    private static readonly Dictionary<char, string> Other = new()
    {
        ['ж'] = "zh", ['ц'] = "c", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['й'] = "j",
    };

    private static readonly Dictionary<char, string> VowelMap = new()
    {
        ['а'] = "a", ['я'] = "a", ['у'] = "u", ['ю'] = "u", ['о'] = "o", ['ё'] = "o", ['э'] = "e", ['е'] = "e",
        ['и'] = "i", ['ы'] = "y",
    };

    public static bool IsConvertible(string word) =>
        word.Length > 0 && word.All(c => c == '+' || c == '-' || c is >= 'а' and <= 'я' || c == 'ё');

    /// <summary>Возвращает фонемы через пробел или null, если слово нельзя преобразовать.</summary>
    public static string? Convert(string word)
    {
        word = word.ToLowerInvariant().Trim();
        if (!IsConvertible(word)) return null;
        if (!word.Contains('+')) word = PlaceStress(word);

        var chars = ("#" + word + "#").ToCharArray();
        var list = new List<(string Ph, char Orig, int Stress)>();
        int stress = 0;
        foreach (var ch in chars)
        {
            if (ch == '+') { stress = 1; continue; }
            list.Add((ch.ToString(), ch, stress));
            stress = 0;
        }
        // Палатализация.
        for (int i = 0; i < list.Count - 1; i++)
        {
            var c = list[i].Orig;
            if (SoftHard.TryGetValue(c, out var hard))
                list[i] = (SoftLetters.Contains(list[i + 1].Orig) ? hard + "j" : hard, c, 0);
            else if (Other.TryGetValue(c, out var o))
                list[i] = (o, c, 0);
        }
        // Гласные и йотация.
        var phones = new List<string>();
        char prev = '\0';
        foreach (var (ph, orig, st) in list)
        {
            if (prev != '\0' && StartSyl.Contains(prev) && "яюеё".Contains(orig)) phones.Add("j");
            if (VowelMap.TryGetValue(orig, out var v)) phones.Add(v + st);
            else phones.Add(ph);
            prev = orig;
        }
        var result = phones.Where(p => p is not ("#" or "+" or "-" or "ь" or "ъ")).ToList();
        if (result.Count == 0) return null;
        // Последний конечный звонкий не оглушаем — модель обучена на упрощённом словаре без этого.
        return string.Join(' ', result);
    }

    internal static string PlaceStress(string word)
    {
        int yo = word.IndexOf('ё');
        if (yo >= 0) return word.Insert(yo, "+");
        var idx = new List<int>();
        for (int i = 0; i < word.Length; i++) if (Vowels.Contains(word[i])) idx.Add(i);
        if (idx.Count == 0) return word;
        int pos = idx.Count == 1 ? idx[0] : idx[^2];
        return word.Insert(pos, "+");
    }
}
