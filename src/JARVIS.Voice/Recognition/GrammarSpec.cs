using System.Text;
using System.Text.RegularExpressions;
using Jarvis.Core.Commands;
using Jarvis.Core.Text;

namespace Jarvis.Voice.Recognition;

/// <summary>
/// Словарь распознавания, построенный из того же словаря команд, что и парсер.
/// Распознаватель слышит только предусмотренные фразы (плюс «мусорные» слова,
/// поглощающие посторонний разговор).
/// </summary>
public sealed partial class GrammarSpec
{
    public static readonly string[] OpenVerbs = ["открой", "запусти", "открыть", "запустить", "open", "launch", "start"];
    public static readonly string[] SwitchVerbs = ["переключись на", "переключи на", "перейди в", "перейди к", "перейди на", "покажи", "активируй", "switch to"];
    public static readonly string[] CloseVerbs = ["закрой", "закрыть", "close"];
    public static readonly string[] AppVerbsOther = ["сверни", "разверни"];
    public static readonly string[] SiteVerbs = ["открой сайт", "зайди на", "зайди на сайт", "перейди на сайт", "открой"];
    public static readonly string[] TemplateVerbs = ["запусти шаблон", "выполни шаблон", "шаблон"];
    public static readonly string[] VolumeVerbs = ["громкость", "громкость на", "установи громкость"];
    public static readonly string[] NameVerbs = ["назови его", "назови шаблон", "назови", "запомни как", "запомни это как"];
    public static readonly string[] StepAppVerbs = ["запуск", "запуск программы", "открытие", "переключение на", "переключение в", "закрытие", "сворачивание", "разворачивание", "ожидание"];
    public static readonly string[] StepWaitVerbs = ["ожидание", "паузу"];
    public static readonly string[] TimeUnits = ["секунд", "секунды", "секунду", "секунда", "минут", "минуты", "минуту"];
    public static readonly string[] StepFixed = ["скриншот", "снимок экрана", "сворачивание окна", "разворачивание окна", "рабочий стол", "завершение шаблона"];

    /// <summary>Распространённые названия для голосового создания шаблонов.</summary>
    public static readonly string[] DefaultNames =
        ["работа", "работу", "учеба", "учебу", "игры", "отдых", "утро", "вечер", "музыка", "музыку", "кино", "стрим", "почта", "почту",
         "новости", "код", "проект", "созвон", "встреча", "кнопка", "кнопку", "поле", "отправить", "поиск", "меню", "закрыть", "назад",
         "один", "два", "три", "четыре", "пять"];

    /// <summary>Частые слова для поглощения постороннего разговора (классический приём «garbage model»).</summary>
    public static readonly string[] GarbageWords =
        ["и", "в", "не", "на", "я", "что", "он", "с", "это", "как", "а", "то", "все", "она", "так", "его", "но", "ты", "по", "из",
         "у", "за", "вы", "же", "от", "бы", "мы", "о", "к", "вот", "ну", "там", "тут", "когда", "уже", "где", "кто", "был", "есть",
         "нет", "если", "или", "мне", "меня", "тебя", "сейчас", "потом", "очень", "просто", "можно", "надо", "хорошо", "ладно",
         "давай", "слушай", "смотри", "короче", "типа", "вообще", "ага", "угу", "понятно", "спасибо", "привет", "пока", "день",
         "время", "раз", "год", "человек", "дело", "жизнь", "сегодня", "завтра", "вчера", "сказал", "знаю", "думаю", "хочу", "буду"];

    public List<string> WakePhrases { get; init; } = [];
    public string StopWord { get; init; } = "";
    public List<string> AppNames { get; init; } = [];
    public List<string> SiteNames { get; init; } = [];
    public List<string> TemplatePhrases { get; init; } = [];
    public List<string> Names { get; init; } = [];
    public bool IncludeGarbage { get; init; } = true;

    [GeneratedRegex("^[а-яёa-z]+(?: [а-яёa-z]+)*$")]
    private static partial Regex ValidPhrase();

    public static string Clean(string phrase)
    {
        var p = phrase.ToLowerInvariant().Replace('-', ' ');
        p = Regex.Replace(p, @"[^\p{L}\s]", " ");
        return Regex.Replace(p, @"\s+", " ").Trim();
    }

    private static IEnumerable<string> Valid(IEnumerable<string> phrases) =>
        phrases.Select(Clean).Where(p => p.Length > 0 && ValidPhrase().IsMatch(p)).Distinct();

    public IEnumerable<string> FixedPhrases() => Valid(CommandLexicon.Fixed.Keys);

    public IEnumerable<string> AllWords()
    {
        var phrases = new List<string>();
        phrases.AddRange(WakePhrases); phrases.Add(StopWord); phrases.AddRange(AppNames); phrases.AddRange(SiteNames);
        phrases.AddRange(TemplatePhrases); phrases.AddRange(Names); phrases.AddRange(FixedPhrases());
        phrases.AddRange(OpenVerbs); phrases.AddRange(SwitchVerbs); phrases.AddRange(CloseVerbs); phrases.AddRange(AppVerbsOther);
        phrases.AddRange(SiteVerbs); phrases.AddRange(TemplateVerbs); phrases.AddRange(VolumeVerbs); phrases.AddRange(NameVerbs);
        phrases.AddRange(StepAppVerbs); phrases.AddRange(StepWaitVerbs); phrases.AddRange(TimeUnits); phrases.AddRange(StepFixed);
        phrases.AddRange(RussianNumbers.NumberWords); phrases.AddRange(CommandLexicon.AppFillers); phrases.Add("добавь"); phrases.Add("открытие сайта");
        if (IncludeGarbage) phrases.AddRange(GarbageWords);
        return Valid(phrases).SelectMany(p => p.Split(' ')).Distinct();
    }

    /// <summary>JSGF-грамматика для PocketSphinx.</summary>
    public string ToJsgf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#JSGF V1.0;");
        sb.AppendLine("grammar jarvis;");
        var rules = new Dictionary<string, string?>
        {
            ["wake"] = Alt(WakePhrases),
            ["stop"] = Alt([StopWord]),
            ["app"] = Alt(AppNames),
            ["site"] = Alt(SiteNames),
            ["tpl"] = Alt(TemplatePhrases),
            ["name"] = Alt(Names.Concat(TemplatePhrases)),
            ["n"] = Alt(RussianNumbers.NumberWords),
            ["unit"] = Alt(TimeUnits),
            ["fixed"] = Alt(FixedPhrases()),
            ["garbage"] = IncludeGarbage ? Alt(GarbageWords) : null,
        };
        var cmd = new List<string> { "<fixed>" };
        if (rules["app"] is not null)
            cmd.Add($"{Alt(OpenVerbs.Concat(SwitchVerbs).Concat(CloseVerbs).Concat(AppVerbsOther))} [{Alt(CommandLexicon.AppFillers)}] <app>");
        if (rules["site"] is not null) cmd.Add($"{Alt(SiteVerbs)} <site>");
        if (rules["tpl"] is not null) { cmd.Add($"{Alt(TemplateVerbs)} <tpl>"); cmd.Add("<tpl>"); }
        cmd.Add($"{Alt(VolumeVerbs)} <num>");
        cmd.Add($"{Alt(NameVerbs)} <name>");
        var step = new List<string> { Alt(StepFixed)!, $"{Alt(StepWaitVerbs)} <num> <unit>" };
        if (rules["app"] is not null) step.Add($"{Alt(StepAppVerbs)} <app>");
        if (rules["site"] is not null) step.Add("открытие сайта <site>");
        if (rules["tpl"] is not null) step.Add("шаблон <tpl>");
        cmd.Add("добавь <step>");

        var top = new List<string>();
        if (rules["wake"] is not null) top.Add("<wake> [<cmd>]");
        top.Add("<cmd>");
        if (rules["stop"] is not null) top.Add("<stop>");
        if (rules["garbage"] is not null) top.Add("<garbage>+");
        sb.AppendLine($"public <utt> = {string.Join(" | ", top)};");
        foreach (var (name, body) in rules)
            if (body is not null) sb.AppendLine($"<{name}> = {body};");
        sb.AppendLine("<num> = <n> [<n>] [<n>];");
        sb.AppendLine($"<step> = {string.Join(" | ", step.Select(s => "(" + s + ")"))};");
        sb.AppendLine($"<cmd> = {string.Join(" | ", cmd.Select(s => "(" + s + ")"))};");
        return sb.ToString();
    }

    /// <summary>Плоский список фраз (для SAPI): с фразой активации и без неё.</summary>
    public IReadOnlyList<string> ExpandPhrases(int max = 20000)
    {
        var cmds = ExpandCommands();
        var all = new List<string>();
        foreach (var w in Valid(WakePhrases))
        {
            all.Add(w);
            all.AddRange(cmds.Select(c => $"{w} {c}"));
        }
        all.AddRange(cmds);
        if (StopWord.Length > 0) all.Add(Clean(StopWord));
        return all.Distinct().Take(max).ToList();
    }

    /// <summary>Все команды без фразы активации.</summary>
    public List<string> ExpandCommands()
    {
        var cmds = new List<string>(FixedPhrases());
        var apps = Valid(AppNames).ToList();
        foreach (var v in OpenVerbs.Concat(SwitchVerbs).Concat(CloseVerbs).Concat(AppVerbsOther))
            foreach (var a in apps)
            {
                cmds.Add($"{v} {a}");
                if (OpenVerbs.Contains(v)) foreach (var f in CommandLexicon.AppFillers) cmds.Add($"{v} {f} {a}");
            }
        foreach (var v in SiteVerbs) foreach (var s in Valid(SiteNames)) cmds.Add($"{v} {s}");
        foreach (var t in Valid(TemplatePhrases)) { cmds.Add(t); foreach (var v in TemplateVerbs) cmds.Add($"{v} {t}"); }
        foreach (var n in Enumerable.Range(0, 101)) cmds.Add($"громкость {NumberToWords(n)}");
        foreach (var name in Valid(Names.Concat(TemplatePhrases))) foreach (var v in NameVerbs) cmds.Add($"{v} {name}");
        foreach (var v in StepAppVerbs) foreach (var a in apps) cmds.Add($"добавь {v} {a}");
        foreach (var f in StepFixed) cmds.Add($"добавь {f}");
        foreach (var s in new[] { 1, 2, 3, 5, 10, 15, 20, 30 }) cmds.Add($"добавь ожидание {NumberToWords(s)} секунд");
        return cmds.Distinct().ToList();
    }

    public static string NumberToWords(int n)
    {
        string[] units = ["ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять", "десять",
            "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"];
        string[] tens = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];
        if (n < 20) return units[n];
        if (n == 100) return "сто";
        return n % 10 == 0 ? tens[n / 10] : $"{tens[n / 10]} {units[n % 10]}";
    }

    private static string? Alt(IEnumerable<string> phrases)
    {
        var list = Valid(phrases).ToList();
        return list.Count == 0 ? null : "(" + string.Join(" | ", list) + ")";
    }

    /// <summary>Удаляет «мусорные» слова из гипотезы. Пустой результат = посторонний разговор.</summary>
    public static string StripGarbage(string hyp, GrammarSpec spec)
    {
        var words = TextNormalizer.Words(TextNormalizer.Normalize(hyp));
        var meaningful = spec.AllWords().Except(GarbageWords).Select(TextNormalizer.Normalize).ToHashSet();
        // Если слово одновременно и командное, и мусорное («нет», «два»), оно сохраняется.
        var kept = words.Where(w => meaningful.Contains(w) || !GarbageWords.Contains(w)).ToArray();
        return IsAllGarbage(words) ? "" : string.Join(' ', kept);
    }

    private static bool IsAllGarbage(string[] words) => words.All(w => GarbageWords.Contains(w));
}

public sealed record RecognitionResult(string Text, double Confidence);

/// <summary>Офлайн-распознаватель фраз (Vosk, PocketSphinx или Windows SAPI).</summary>
public interface ISpeechEngine : IDisposable
{
    string Name { get; }
    bool IsReady { get; }
    string Status { get; }
    void Initialize(GrammarSpec grammar);
    void UpdateGrammar(GrammarSpec grammar);
    RecognitionResult? Recognize(short[] samples);
}
