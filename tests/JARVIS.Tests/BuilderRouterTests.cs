using Jarvis.Automation;
using Jarvis.Core;
using Jarvis.Core.Apps;
using Jarvis.Core.Commands;
using Jarvis.Templates;

namespace Jarvis.Tests;

public class BuilderRouterTests
{
    private static (VoiceTemplateBuilder, TemplateStore, CommandParser, TempDir) Setup()
    {
        var dir = new TempDir();
        var store = new TemplateStore(Path.Combine(dir.Path, "t"));
        var catalog = new AppCatalog();
        var builder = new VoiceTemplateBuilder(() => catalog.All, store);
        var parser = new CommandParser(() => catalog.All, store.Phrases);
        return (builder, store, parser, dir);
    }

    [Fact]
    public void VoiceCreation_FullFlow_UsesSameBlockModel()
    {
        var (b, store, parser, dir) = Setup();
        using var _ = dir;
        b.Handle(parser.Parse("создай шаблон"));
        Assert.True(b.IsActive);
        b.Handle(parser.Parse("назови его работа"));
        Assert.Equal("Работа", b.Draft!.Name);
        b.Handle(parser.Parse("добавь запуск chrome"));
        b.Handle(parser.Parse("добавь переключение на telegram"));
        b.Handle(parser.Parse("добавь ожидание 5 секунд"));
        b.Handle(parser.Parse("добавь открытие сайта youtube.com"));
        var reply = b.Handle(parser.Parse("сохрани шаблон"));
        Assert.True(reply.Finished);
        var t = store.FindByName("работа")!;
        Assert.Equal(4, t.Steps.Count);
        Assert.Equal(StepKind.LaunchApp, t.Steps[0].Kind);
        Assert.Equal("Google Chrome", t.Steps[0].Get(P.App));
        Assert.Equal(StepKind.SwitchToWindow, t.Steps[1].Kind);
        Assert.Equal("5000", t.Steps[2].Get(P.Ms));
        Assert.Equal("https://youtube.com", t.Steps[3].Get(P.Url));
        // Шаблон сразу доступен голосом.
        Assert.Equal(CommandIntent.RunTemplate, parser.Parse("работа").Intent);
    }

    [Fact]
    public void VoiceCreation_UnclearStep_IsNotAdded()
    {
        var (b, _, parser, dir) = Setup();
        using var _d = dir;
        b.Handle(parser.Parse("создай шаблон"));
        var r = b.Handle(parser.Parse("добавь что нибудь интересное"));
        Assert.Empty(b.Draft!.Steps);
        Assert.Contains("Не понял", r.Speech);
    }

    [Fact]
    public void VoiceCreation_Cancel_DiscardsDraft()
    {
        var (b, store, parser, dir) = Setup();
        using var _d = dir;
        b.Handle(parser.Parse("создай шаблон"));
        b.Handle(parser.Parse("назови его игры"));
        b.Handle(parser.Parse("отмени создание шаблона"));
        Assert.False(b.IsActive);
        Assert.Empty(store.All);
    }

    [Fact]
    public void VoiceCreation_SaveWithoutNameOrSteps_Refused()
    {
        var (b, store, parser, dir) = Setup();
        using var _d = dir;
        b.Handle(parser.Parse("создай шаблон"));
        Assert.Contains("назовите", b.Handle(parser.Parse("сохрани шаблон")).Speech, StringComparison.OrdinalIgnoreCase);
        b.Handle(parser.Parse("назови его утро"));
        Assert.Contains("нет действий", b.Handle(parser.Parse("сохрани шаблон")).Speech);
        Assert.Empty(store.All);
    }

    [Fact]
    public void Router_DirectCommand_BecomesSingleStep()
    {
        var (b, store, parser, dir) = Setup();
        using var _d = dir;
        var router = new CommandRouter(store, b);
        var r = Assert.IsType<RunStepsRoute>(router.Route(parser.Parse("открой хром")));
        Assert.Equal(StepKind.LaunchApp, r.Steps[0].Kind);
        Assert.IsType<ControlRoute>(router.Route(parser.Parse("отключись")));
        Assert.IsType<SpeakRoute>(router.Route(parser.Parse("свари борщ")));
    }

    [Fact]
    public void Router_TypeText_PreservesOriginalCase()
    {
        var (b, store, parser, dir) = Setup();
        using var _d = dir;
        var router = new CommandRouter(store, b);
        const string raw = "Напечатай Привет, Мир!";
        var r = Assert.IsType<RunStepsRoute>(router.Route(parser.Parse(raw), raw));
        Assert.Equal("Привет, Мир!", r.Steps[0].Get(P.Text));
    }

    [Theory]
    [InlineData("Ctrl+Shift+T", "Ctrl+Shift+T")]
    [InlineData("shift+ctrl+t", "Ctrl+Shift+T")]
    [InlineData("Alt+F4", "Alt+F4")]
    [InlineData("enter", "Enter")]
    [InlineData("Win+D", "Win+D")]
    [InlineData("Ctrl++", "Ctrl+Plus")]
    public void KeyChord_Parses(string text, string expected)
    {
        Assert.True(KeyChord.TryParse(text, out var c));
        Assert.Equal(expected, c.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Ж")]
    [InlineData("T+Ctrl")]
    public void KeyChord_RejectsInvalid(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void Validator_DetectsCycleAndMissingParams()
    {
        var repo = new InMemoryRepo();
        var a = new Template { Name = "A", Steps = [Steps.Of(StepKind.RunTemplate, (P.Template, "B"))] };
        var b = new Template { Name = "B", Steps = [Steps.Of(StepKind.RunTemplate, (P.Template, "A"))] };
        repo.Items.AddRange([a, b]);
        Assert.Contains(TemplateValidator.Validate(a, repo), i => i.Message.Contains("Циклический"));
        var c = new Template { Name = "C", Steps = [StepCatalog.Create(StepKind.TypeText)] };
        Assert.Contains(TemplateValidator.Validate(c, repo), i => i.IsError && i.Message.Contains("Текст"));
    }
}
