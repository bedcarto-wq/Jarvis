namespace Jarvis.Core.Text;

/// <summary>Разбор чисел 0..9999, записанных цифрами или русскими словами.</summary>
public static class RussianNumbers
{
    private static readonly Dictionary<string, int> Units = new()
    {
        ["ноль"] = 0, ["один"] = 1, ["одна"] = 1, ["одну"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3,
        ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9,
        ["десять"] = 10, ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13,
        ["четырнадцать"] = 14, ["пятнадцать"] = 15, ["шестнадцать"] = 16, ["семнадцать"] = 17,
        ["восемнадцать"] = 18, ["девятнадцать"] = 19, ["двадцать"] = 20, ["тридцать"] = 30,
        ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60, ["семьдесят"] = 70,
        ["восемьдесят"] = 80, ["девяносто"] = 90, ["сто"] = 100, ["двести"] = 200, ["триста"] = 300,
        ["четыреста"] = 400, ["пятьсот"] = 500, ["шестьсот"] = 600, ["семьсот"] = 700,
        ["восемьсот"] = 800, ["девятьсот"] = 900, ["тысяча"] = 1000, ["тысячу"] = 1000,
        ["пол"] = 0,
    };

    public static IEnumerable<string> NumberWords => Units.Keys.Where(k => k != "пол");

    /// <summary>Ищет первое число в словах; возвращает индекс начала и длину в словах.</summary>
    public static bool TryFind(string[] words, out int value, out int start, out int length)
    {
        value = 0; start = -1; length = 0;
        for (int i = 0; i < words.Length; i++)
        {
            if (int.TryParse(words[i].TrimEnd('%'), out var d))
            {
                value = d; start = i; length = 1;
                return true;
            }
            if (Units.ContainsKey(words[i]) && words[i] != "пол")
            {
                int sum = 0, j = i;
                while (j < words.Length && Units.TryGetValue(words[j], out var u) && words[j] != "пол")
                {
                    sum += u;
                    j++;
                }
                value = sum; start = i; length = j - i;
                return true;
            }
        }
        return false;
    }

    public static bool TryParse(string text, out int value)
    {
        var w = TextNormalizer.Words(TextNormalizer.Normalize(text));
        return TryFind(w, out value, out _, out _);
    }
}
