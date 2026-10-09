using Jarvis.Core.Apps;
using Jarvis.Core.Commands;
using Jarvis.Core.Settings;
using Jarvis.Voice.Recognition;

namespace Jarvis.Tests;

/// <summary>
/// Тестовый набор русских голосовых команд (текстовый уровень: фраза → намерение).
/// Проверяет разбор и то, что все слова команд есть в словаре распознавателя.
/// Точность распознавания живой речи этим набором не измеряется.
/// </summary>
public class RussianCommandSetTests
{
    private static readonly string[] Wake = ["джарвис", "окей джарвис", "эй джарвис"];
    private static readonly CommandParser Parser = new(() => new AppCatalog().All, () => [new TemplatePhrase(Guid.NewGuid().ToString(), "Работа", "рабочий режим")], () => Wake);

    public static TheoryData<string, CommandIntent, string?> Set => new()
    {
        { "открой браузер Chrome", CommandIntent.OpenApp, "chrome" },
        { "открой браузер хром", CommandIntent.OpenApp, "chrome" },
        { "запусти программу блокнот", CommandIntent.OpenApp, "notepad" },
        { "открой приложение телеграм", CommandIntent.OpenApp, "telegram" },
        { "открой телеграмм", CommandIntent.OpenApp, "telegram" },
        { "открой браузер", CommandIntent.OpenApp, "browser" },
        { "запусти калькулятор", CommandIntent.OpenApp, "calc" },
        { "переключись на хром", CommandIntent.SwitchTo, "chrome" },
        { "перейди в телеграм", CommandIntent.SwitchTo, "telegram" },
        { "закрой блокнот", CommandIntent.CloseApp, "notepad" },
        { "сверни проводник", CommandIntent.MinimizeApp, "explorer" },
        { "разверни хром", CommandIntent.MaximizeApp, "chrome" },
        { "сверни окно", CommandIntent.MinimizeActive, null },
        { "закрой окно", CommandIntent.CloseActive, null },
        { "покажи рабочий стол", CommandIntent.ShowDesktop, null },
        { "открой ютуб", CommandIntent.OpenSite, null },
        { "зайди на сайт гитхаб", CommandIntent.OpenSite, null },
        { "новая вкладка", CommandIntent.NewTab, null },
        { "громче", CommandIntent.VolumeUp, null },
        { "сделай тише", CommandIntent.VolumeDown, null },
        { "громкость тридцать", CommandIntent.SetVolume, null },
        { "выключи звук", CommandIntent.Mute, null },
        { "напиши привет мир", CommandIntent.TypeText, null },
        { "скопируй", CommandIntent.Copy, null },
        { "вставь", CommandIntent.Paste, null },
        { "сделай скриншот", CommandIntent.Screenshot, null },
        { "запусти шаблон работа", CommandIntent.RunTemplate, null },
        { "рабочий режим", CommandIntent.RunTemplate, null },
        { "создай шаблон", CommandIntent.CreateTemplate, null },
        { "назови его утро", CommandIntent.NameTemplate, null },
        { "добавь запуск телеграм", CommandIntent.AddStep, null },
        { "сохрани шаблон", CommandIntent.SaveTemplate, null },
        { "запомни это как кнопка отправить", CommandIntent.Remember, null },
        { "здесь", CommandIntent.PointHere, null },
        { "да", CommandIntent.Confirm, null },
        { "начать", CommandIntent.Confirm, null },
        { "нет", CommandIntent.Deny, null },
        { "стоп", CommandIntent.Stop, null },
        { "пауза", CommandIntent.Pause, null },
        { "продолжай", CommandIntent.Resume, null },
        { "отключись", CommandIntent.Disable, null },
        { "включись", CommandIntent.Enable, null },
        { "проведи бенчмарк", CommandIntent.Benchmark, null },
        { "запусти бенчмарк", CommandIntent.Benchmark, null },
        { "проверь производительность", CommandIntent.Benchmark, null },
        { "что ты умеешь", CommandIntent.Help, null },
    };

    [Theory]
    [MemberData(nameof(Set))]
    public void Phrase_MapsToIntent(string phrase, CommandIntent intent, string? appId)
    {
        var c = Parser.Parse(phrase);
        Assert.Equal(intent, c.Intent);
        if (appId is not null) Assert.Equal(appId, c.App!.Best!.App.Id);
    }

    [Theory]
    [InlineData("джарвис открой хром")]
    [InlineData("Окей, Джарвис, открой хром")]
    [InlineData("эй джарвис открой хром")]
    public void WakeVariants_AreStripped(string text)
    {
        Assert.True(WakePhraseDetector.TryStrip(text, Wake, out var rest));
        Assert.Equal(CommandIntent.OpenApp, Parser.Parse(rest).Intent);
    }

    [Theory]
    [InlineData("открой фотошоп которого нет")]
    [InlineData("сделай мне бутерброд")]
    [InlineData("")]
    public void UnknownOrEmpty_IsNotGuessed(string text)
    {
        var c = Parser.Parse(text);
        Assert.Contains(c.Intent, new[] { CommandIntent.Unknown, CommandIntent.Empty });
    }

    [Fact]
    public void AllWordsOfTheSet_AreInRecognizerVocabulary()
    {
        var spec = VocabularyBuilder.Build(new JarvisSettings(), BuiltInApps.All.Select(BuiltInApps.ToEntry), ["рабочий режим"],
            ["утро", "кнопка отправить", "работа"]);
        var words = spec.AllWords().ToHashSet();
        var skip = new HashSet<string>();
        foreach (var row in Set)
        {
            var phrase = GrammarSpec.Clean((string)row[0]);
            if ((CommandIntent)row[1] == CommandIntent.TypeText) continue; // свободная диктовка — вне грамматики (см. VOICE.md)
            foreach (var w in phrase.Split(' ').Where(w => !skip.Contains(w)))
                Assert.True(words.Contains(w), $"«{w}» из «{phrase}» нет в словаре");
        }
        var jsgf = spec.ToJsgf();
        Assert.Contains("бенчмарк", jsgf);
        Assert.Contains("браузер", jsgf);
    }

    [Fact]
    public void ShortReplies_AreTheSevenRequired()
    {
        Assert.Equal(new[] { "Готово", "Выполняю", "Не могу", "Повторите команду", "Наведите курсор", "Подтвердите действие", "Остановлено" }, VoiceReplies.All);
    }
}
