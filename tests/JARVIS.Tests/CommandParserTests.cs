using Jarvis.Core.Apps;
using Jarvis.Core.Commands;
using Jarvis.Core.Text;

namespace Jarvis.Tests;

public class CommandParserTests
{
    private static readonly string[] Wake = ["джарвис", "окей джарвис", "эй джарвис"];

    private static CommandParser Parser(params TemplatePhrase[] templates) =>
        new(() => new AppCatalog().All, () => templates, () => Wake);

    [Theory]
    [InlineData("открой chrome")]
    [InlineData("Запусти Chrome")]
    [InlineData("открой браузер Chrome")]
    [InlineData("открой хром")]
    [InlineData("запусти гугл хром")]
    public void OpenChromeVariants_MapToSameAction(string text)
    {
        var c = Parser().Parse(text);
        Assert.Equal(CommandIntent.OpenApp, c.Intent);
        Assert.Equal("chrome", c.App!.Best!.App.Id);
    }

    [Theory]
    [InlineData("открой клод", "claude")]
    [InlineData("открой Claude", "claude")]
    [InlineData("запусти телеграм", "telegram")]
    [InlineData("открой дискорд", "discord")]
    public void RussianPronunciation_MapsToApp(string text, string id)
    {
        var c = Parser().Parse(text);
        Assert.Equal(CommandIntent.OpenApp, c.Intent);
        Assert.Equal(id, c.App!.Best!.App.Id);
    }

    [Theory]
    [InlineData("переключись на telegram", CommandIntent.SwitchTo, "telegram")]
    [InlineData("переключись на телеграм", CommandIntent.SwitchTo, "telegram")]
    [InlineData("закрой discord", CommandIntent.CloseApp, "discord")]
    [InlineData("закрой дискорд", CommandIntent.CloseApp, "discord")]
    public void SwitchAndClose(string text, CommandIntent intent, string id)
    {
        var c = Parser().Parse(text);
        Assert.Equal(intent, c.Intent);
        Assert.Equal(id, c.App!.Best!.App.Id);
    }

    [Theory]
    [InlineData("сверни окно", CommandIntent.MinimizeActive)]
    [InlineData("разверни окно", CommandIntent.MaximizeActive)]
    [InlineData("покажи рабочий стол", CommandIntent.ShowDesktop)]
    [InlineData("сделай скриншот", CommandIntent.Screenshot)]
    [InlineData("громче", CommandIntent.VolumeUp)]
    [InlineData("отключись", CommandIntent.Disable)]
    [InlineData("включись", CommandIntent.Enable)]
    [InlineData("создай шаблон", CommandIntent.CreateTemplate)]
    [InlineData("отмени создание шаблона", CommandIntent.CancelTemplate)]
    [InlineData("сохрани шаблон", CommandIntent.SaveTemplate)]
    [InlineData("новая вкладка", CommandIntent.NewTab)]
    [InlineData("открой новую вкладку", CommandIntent.NewTab)]
    [InlineData("да", CommandIntent.Confirm)]
    [InlineData("отмена", CommandIntent.Deny)]
    public void FixedPhrases(string text, CommandIntent intent) => Assert.Equal(intent, Parser().Parse(text).Intent);

    [Fact]
    public void OpenSite_ByAliasAndDomain()
    {
        var a = Parser().Parse("открой ютуб");
        Assert.Equal(CommandIntent.OpenSite, a.Intent);
        Assert.Equal("https://www.youtube.com", a.Url);
        var b = Parser().Parse("открой сайт github.com");
        Assert.Equal("https://github.com", b.Url);
        var c = Parser().Parse("зайди на github точка com");
        Assert.Equal("https://github.com", c.Url);
    }

    [Fact]
    public void SetVolume_ParsesRussianNumber()
    {
        var c = Parser().Parse("громкость пятьдесят");
        Assert.Equal(CommandIntent.SetVolume, c.Intent);
        Assert.Equal(50, c.Number);
        Assert.Equal(35, Parser().Parse("громкость на тридцать пять").Number);
        Assert.Equal(100, Parser().Parse("громкость 250").Number);
    }

    [Fact]
    public void TemplatePhrase_HasPriority()
    {
        var id = Guid.NewGuid().ToString();
        var p = Parser(new TemplatePhrase(id, "Работа", "Джарвис, работа"));
        var c = p.Parse("работа");
        Assert.Equal(CommandIntent.RunTemplate, c.Intent);
        Assert.Equal(id, c.TemplateId);
        var d = p.Parse("запусти шаблон работа");
        Assert.Equal(CommandIntent.RunTemplate, d.Intent);
    }

    [Fact]
    public void UnknownTask_IsNotFaked()
    {
        var c = Parser().Parse("приготовь мне кофе");
        Assert.Equal(CommandIntent.Unknown, c.Intent);
        Assert.Contains("шаблон", c.Message);
        var d = Parser().Parse("открой абракадабру");
        Assert.Equal(CommandIntent.Unknown, d.Intent);
    }

    [Fact]
    public void AmbiguousApp_RequestsClarification()
    {
        var apps = new List<AppEntry>
        {
            new() { Id = "p1", DisplayName = "Player One" },
            new() { Id = "p2", DisplayName = "Player Two" },
        };
        var parser = new CommandParser(() => apps, () => []);
        var c = parser.Parse("открой player");
        Assert.Equal(CommandIntent.Ambiguous, c.Intent);
        Assert.Equal(2, c.App!.Candidates.Count);
        Assert.Contains("Уточните", c.Message);
    }

    [Theory]
    [InlineData("Джарвис, открой Chrome", true, "открой chrome")]
    [InlineData("Окей, Джарвис, открой Chrome", true, "открой chrome")]
    [InlineData("эй джарвис сверни окно", true, "сверни окно")]
    [InlineData("открой chrome", false, "открой chrome")]
    [InlineData("джарвисон", false, "джарвисон")]
    public void WakePhrase_IsStripped(string text, bool expected, string rest)
    {
        Assert.Equal(expected, WakePhraseDetector.TryStrip(text, Wake, out var r));
        Assert.Equal(rest, r);
    }

    [Theory]
    [InlineData("жёпа", true)]
    [InlineData("жепа", true)]
    [InlineData("джарвис жёпа", true)]
    [InlineData("жёпать", false)]
    public void StopWord_DetectedWithYoNormalization(string text, bool expected) =>
        Assert.Equal(expected, WakePhraseDetector.ContainsStopWord(text, "жёпа"));

    [Fact]
    public void Normalizer_HandlesPunctuationAndYo()
    {
        Assert.Equal("ёлка", "ёлка"); // sanity
        Assert.Equal("елка зеленая", TextNormalizer.Normalize("  Ёлка,   зелёная! "));
    }
}
