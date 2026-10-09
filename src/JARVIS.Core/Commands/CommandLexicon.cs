namespace Jarvis.Core.Commands;

/// <summary>
/// Словарь команд: синонимы и альтернативные формулировки. Используется и парсером,
/// и генератором грамматики распознавателя речи, поэтому набор фраз един.
/// </summary>
public static class CommandLexicon
{
    public static readonly IReadOnlyDictionary<string, CommandIntent> Fixed = new Dictionary<string, CommandIntent>
    {
        ["покажи рабочий стол"] = CommandIntent.ShowDesktop, ["рабочий стол"] = CommandIntent.ShowDesktop,
        ["сверни все"] = CommandIntent.ShowDesktop, ["сверни все окна"] = CommandIntent.ShowDesktop,
        ["show desktop"] = CommandIntent.ShowDesktop,
        ["сверни"] = CommandIntent.MinimizeActive, ["сверни окно"] = CommandIntent.MinimizeActive, ["minimize"] = CommandIntent.MinimizeActive,
        ["разверни"] = CommandIntent.MaximizeActive, ["разверни окно"] = CommandIntent.MaximizeActive, ["maximize"] = CommandIntent.MaximizeActive,
        ["закрой окно"] = CommandIntent.CloseActive, ["закрой это окно"] = CommandIntent.CloseActive,
        ["громче"] = CommandIntent.VolumeUp, ["прибавь громкость"] = CommandIntent.VolumeUp, ["увеличь громкость"] = CommandIntent.VolumeUp,
        ["сделай громче"] = CommandIntent.VolumeUp, ["volume up"] = CommandIntent.VolumeUp,
        ["тише"] = CommandIntent.VolumeDown, ["убавь громкость"] = CommandIntent.VolumeDown, ["уменьши громкость"] = CommandIntent.VolumeDown,
        ["сделай тише"] = CommandIntent.VolumeDown, ["volume down"] = CommandIntent.VolumeDown,
        ["выключи звук"] = CommandIntent.Mute, ["отключи звук"] = CommandIntent.Mute, ["без звука"] = CommandIntent.Mute, ["mute"] = CommandIntent.Mute,
        ["включи звук"] = CommandIntent.Unmute, ["unmute"] = CommandIntent.Unmute,
        ["сделай скриншот"] = CommandIntent.Screenshot, ["скриншот"] = CommandIntent.Screenshot, ["снимок экрана"] = CommandIntent.Screenshot,
        ["screenshot"] = CommandIntent.Screenshot,
        ["новая вкладка"] = CommandIntent.NewTab, ["открой новую вкладку"] = CommandIntent.NewTab, ["new tab"] = CommandIntent.NewTab,
        ["следующая вкладка"] = CommandIntent.NextTab, ["next tab"] = CommandIntent.NextTab,
        ["предыдущая вкладка"] = CommandIntent.PrevTab, ["previous tab"] = CommandIntent.PrevTab,
        ["закрой вкладку"] = CommandIntent.CloseTab, ["close tab"] = CommandIntent.CloseTab,
        ["скопируй"] = CommandIntent.Copy, ["копируй"] = CommandIntent.Copy, ["copy"] = CommandIntent.Copy,
        ["вставь"] = CommandIntent.Paste, ["paste"] = CommandIntent.Paste,
        ["выдели все"] = CommandIntent.SelectAll, ["select all"] = CommandIntent.SelectAll,
        ["отмени действие"] = CommandIntent.Undo, ["отмени последнее действие"] = CommandIntent.Undo, ["undo"] = CommandIntent.Undo,
        ["запомни"] = CommandIntent.Remember, ["remember"] = CommandIntent.Remember,
        ["здесь"] = CommandIntent.PointHere, ["вот здесь"] = CommandIntent.PointHere, ["тут"] = CommandIntent.PointHere,
        ["вот"] = CommandIntent.PointHere, ["here"] = CommandIntent.PointHere,
        ["отключись"] = CommandIntent.Disable, ["выключись"] = CommandIntent.Disable, ["спи"] = CommandIntent.Disable,
        ["не слушай"] = CommandIntent.Disable, ["go to sleep"] = CommandIntent.Disable,
        ["включись"] = CommandIntent.Enable, ["проснись"] = CommandIntent.Enable, ["слушай"] = CommandIntent.Enable,
        ["wake up"] = CommandIntent.Enable,
        ["стоп"] = CommandIntent.Stop, ["остановись"] = CommandIntent.Stop, ["stop"] = CommandIntent.Stop,
        ["пауза"] = CommandIntent.Pause, ["приостанови"] = CommandIntent.Pause, ["pause"] = CommandIntent.Pause,
        ["продолжи"] = CommandIntent.Resume, ["продолжай"] = CommandIntent.Resume, ["resume"] = CommandIntent.Resume,
        ["да"] = CommandIntent.Confirm, ["подтверждаю"] = CommandIntent.Confirm, ["выполняй"] = CommandIntent.Confirm, ["начать"] = CommandIntent.Confirm, ["начинай"] = CommandIntent.Confirm,
        ["yes"] = CommandIntent.Confirm, ["confirm"] = CommandIntent.Confirm,
        ["нет"] = CommandIntent.Deny, ["отмена"] = CommandIntent.Deny, ["не надо"] = CommandIntent.Deny,
        ["no"] = CommandIntent.Deny, ["cancel"] = CommandIntent.Deny,
        ["создай шаблон"] = CommandIntent.CreateTemplate, ["новый шаблон"] = CommandIntent.CreateTemplate,
        ["create template"] = CommandIntent.CreateTemplate,
        ["сохрани шаблон"] = CommandIntent.SaveTemplate, ["сохрани"] = CommandIntent.SaveTemplate,
        ["save template"] = CommandIntent.SaveTemplate,
        ["отмени создание шаблона"] = CommandIntent.CancelTemplate, ["отмени шаблон"] = CommandIntent.CancelTemplate,
        ["cancel template"] = CommandIntent.CancelTemplate,
        ["проведи бенчмарк"] = CommandIntent.Benchmark, ["запусти бенчмарк"] = CommandIntent.Benchmark,
        ["бенчмарк"] = CommandIntent.Benchmark, ["проверь производительность"] = CommandIntent.Benchmark,
        ["тест производительности"] = CommandIntent.Benchmark, ["проведи тест производительности"] = CommandIntent.Benchmark,
        ["run benchmark"] = CommandIntent.Benchmark,
        ["что ты умеешь"] = CommandIntent.Help, ["помощь"] = CommandIntent.Help, ["help"] = CommandIntent.Help,
    };

    /// <summary>Слова-уточнения между глаголом и названием программы («открой браузер хром»).</summary>
    public static readonly string[] AppFillers = ["браузер", "программу", "приложение", "игру"];

    /// <summary>Префиксы команд с параметром. Порядок не важен — выбирается самый длинный.</summary>
    public static readonly IReadOnlyList<(string Phrase, CommandIntent Intent)> Prefixes =
    [
        ("открой сайт", CommandIntent.OpenSite), ("зайди на сайт", CommandIntent.OpenSite), ("зайди на", CommandIntent.OpenSite),
        ("перейди на сайт", CommandIntent.OpenSite), ("open site", CommandIntent.OpenSite), ("go to", CommandIntent.OpenSite),
        ("запусти шаблон", CommandIntent.RunTemplate), ("выполни шаблон", CommandIntent.RunTemplate), ("шаблон", CommandIntent.RunTemplate),
        ("run template", CommandIntent.RunTemplate),
        ("назови его", CommandIntent.NameTemplate), ("назови шаблон", CommandIntent.NameTemplate), ("назови", CommandIntent.NameTemplate),
        ("name it", CommandIntent.NameTemplate),
        ("добавь", CommandIntent.AddStep), ("add", CommandIntent.AddStep),
        ("установи громкость", CommandIntent.SetVolume), ("громкость на", CommandIntent.SetVolume), ("громкость", CommandIntent.SetVolume),
        ("set volume", CommandIntent.SetVolume),
        ("открой", CommandIntent.OpenApp), ("запусти", CommandIntent.OpenApp), ("открыть", CommandIntent.OpenApp),
        ("запустить", CommandIntent.OpenApp), ("open", CommandIntent.OpenApp), ("launch", CommandIntent.OpenApp),
        ("start", CommandIntent.OpenApp),
        ("переключись на", CommandIntent.SwitchTo), ("переключи на", CommandIntent.SwitchTo), ("перейди в", CommandIntent.SwitchTo),
        ("перейди к", CommandIntent.SwitchTo), ("перейди на", CommandIntent.SwitchTo), ("покажи", CommandIntent.SwitchTo),
        ("активируй", CommandIntent.SwitchTo), ("switch to", CommandIntent.SwitchTo),
        ("закрой", CommandIntent.CloseApp), ("закрыть", CommandIntent.CloseApp), ("close", CommandIntent.CloseApp),
        ("сверни", CommandIntent.MinimizeApp), ("разверни", CommandIntent.MaximizeApp),
        ("запомни как", CommandIntent.Remember), ("запомни это как", CommandIntent.Remember),
        ("напечатай", CommandIntent.TypeText), ("введи текст", CommandIntent.TypeText), ("напиши", CommandIntent.TypeText),
        ("type", CommandIntent.TypeText),
    ];

    public static readonly IReadOnlyDictionary<string, string> Sites = new Dictionary<string, string>
    {
        ["ютуб"] = "https://www.youtube.com", ["youtube"] = "https://www.youtube.com",
        ["гугл"] = "https://www.google.com", ["google"] = "https://www.google.com",
        ["вконтакте"] = "https://vk.com", ["вк"] = "https://vk.com",
        ["гитхаб"] = "https://github.com", ["github"] = "https://github.com",
        ["почту"] = "https://mail.google.com", ["почта"] = "https://mail.google.com", ["gmail"] = "https://mail.google.com",
        ["википедию"] = "https://ru.wikipedia.org", ["википедия"] = "https://ru.wikipedia.org",
        ["яндекс"] = "https://ya.ru", ["карты"] = "https://yandex.ru/maps",
        ["переводчик"] = "https://translate.yandex.ru",
    };
}
