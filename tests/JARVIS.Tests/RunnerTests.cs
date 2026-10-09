using Jarvis.Core.Apps;
using Jarvis.Templates;

namespace Jarvis.Tests;

public class RunnerTests
{
    [Fact]
    public async Task Launch_AlreadyRunning_SwitchesWithoutRelaunch()
    {
        var h = new RunnerHarness();
        var w = h.Windows.Add("Telegram", "telegram");
        var r = await h.Run(Steps.Of(StepKind.LaunchApp, (P.App, "Telegram")));
        Assert.True(r.Success);
        Assert.Equal(0, h.Launcher.Starts);
        Assert.Equal(w.Handle, h.Windows.Foreground);
    }

    [Fact]
    public async Task Launch_NotRunning_StartsAndVerifiesWindow()
    {
        var h = new RunnerHarness();
        var r = await h.Run(Steps.Of(StepKind.LaunchApp, (P.App, "Google Chrome")));
        Assert.True(r.Success, r.Message);
        Assert.Equal(1, h.Launcher.Starts);
        Assert.Equal("chrome", h.Windows.GetForegroundWindow()!.ProcessName);
    }

    [Fact]
    public async Task Launch_FailsThreeTimes_ThenAsksUserToPoint()
    {
        var h = new RunnerHarness(new FakeVision());
        h.Launcher.Fail = true;
        var r = await h.Run(Steps.Of(StepKind.LaunchApp, (P.App, "Discord")));
        Assert.False(r.Success);
        Assert.False(r.Retryable);
        Assert.Equal(3, h.Launcher.Starts);
        Assert.Equal(1, h.Ui.PointRequests);
    }

    [Fact]
    public async Task Launch_ProcessRunningWithoutWindow_NoBlindDuplicates()
    {
        var h = new RunnerHarness();
        h.Catalog.Upsert(new AppEntry { Id = "editor", DisplayName = "Editor", ProcessNames = ["editor"], LaunchTarget = "editor.exe" });
        h.Windows.BackgroundProcesses.Add("editor");
        h.Launcher.CreateWindow = false;
        var r = await h.Run(Steps.Of(StepKind.LaunchApp, (P.App, "Editor")));
        Assert.False(r.Success);
        Assert.Equal(0, h.Launcher.Starts);
    }

    [Fact]
    public async Task Launch_SingleInstanceApp_RelaunchedToShowWindow()
    {
        var h = new RunnerHarness();
        h.Windows.BackgroundProcesses.Add("telegram"); // свёрнут в трей
        var r = await h.Run(Steps.Of(StepKind.LaunchApp, (P.App, "Telegram")));
        Assert.True(r.Success);
        Assert.Equal(1, h.Launcher.Starts);
    }

    [Fact]
    public async Task Launch_UnknownApp_FailsWithoutRetry()
    {
        var h = new RunnerHarness();
        var r = await h.Run(Steps.Of(StepKind.LaunchApp, (P.App, "Несуществующая программа XYZ")));
        Assert.False(r.Success);
        Assert.False(r.Retryable);
    }

    [Fact]
    public async Task Close_MissingWindow_ReportsError()
    {
        var h = new RunnerHarness();
        var r = await h.Run(Steps.Of(StepKind.CloseWindow, (P.App, "Discord")));
        Assert.False(r.Success);
        Assert.Contains("не найдено", r.Message);
    }

    [Fact]
    public async Task Close_ExistingWindow_VerifiesClosed()
    {
        var h = new RunnerHarness();
        h.Windows.Add("Discord", "discord");
        Assert.True((await h.Run(Steps.Of(StepKind.CloseWindow, (P.App, "Discord")))).Success);
        h.Windows.Add("Discord", "discord");
        h.Windows.IgnoreClose = true;
        var r = await h.Run(Steps.Of(StepKind.CloseWindow, (P.App, "Discord")));
        Assert.False(r.Success);
        Assert.Contains("не закрылось", r.Message);
    }

    [Fact]
    public async Task Switch_MissingWindow_Fails()
    {
        var h = new RunnerHarness();
        var r = await h.Run(Steps.Of(StepKind.SwitchToWindow, (P.App, "Telegram")));
        Assert.False(r.Success);
    }

    [Fact]
    public async Task MinimizeActive_VerifiesState()
    {
        var h = new RunnerHarness();
        var w = h.Windows.Add("Notepad", "notepad");
        h.Windows.Foreground = w.Handle;
        var r = await h.Run(StepCatalog.Create(StepKind.MinimizeWindow));
        Assert.True(r.Success);
        Assert.True(h.Windows.GetWindow(w.Handle)!.IsMinimized);
    }

    [Fact]
    public async Task MoveFile_CanBeUndone()
    {
        var h = new RunnerHarness();
        var src = Path.GetFullPath("/tmp/jarvis-a.txt");
        var dst = Path.GetFullPath("/tmp/jarvis-b.txt");
        h.Files.Files[src] = "data";
        var r = await h.Run(Steps.Of(StepKind.MoveFile, (P.Source, src), (P.Destination, dst)));
        Assert.True(r.Success);
        Assert.True(h.Files.FileExists(dst));
        Assert.True(h.Undo.CanUndo);
        h.Undo.UndoLast();
        Assert.True(h.Files.FileExists(src));
        Assert.False(h.Files.FileExists(dst));
    }

    [Fact]
    public async Task CreateFile_DoesNotOverwriteByDefault()
    {
        var h = new RunnerHarness();
        var p = Path.GetFullPath("/tmp/jarvis-c.txt");
        h.Files.Files[p] = "old";
        var r = await h.Run(Steps.Of(StepKind.CreateFile, (P.Path, p), (P.Content, "new")));
        Assert.False(r.Success);
        Assert.Equal("old", h.Files.Files[p]);
    }

    [Theory]
    [InlineData("file:///C:/Windows/system32/cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("youtube.com", true)]
    [InlineData("https://github.com", true)]
    public async Task OpenUrl_OnlyHttp(string url, bool ok)
    {
        var h = new RunnerHarness();
        var r = await h.Run(Steps.Of(StepKind.OpenUrl, (P.Url, url)));
        Assert.Equal(ok, r.Success);
    }

    [Fact]
    public async Task Volume_SetAndMute()
    {
        var h = new RunnerHarness();
        await h.Run(Steps.Of(StepKind.SetVolume, (P.Mode, "set"), (P.Value, "30")));
        Assert.Equal(30, h.Volume.Level);
        await h.Run(Steps.Of(StepKind.SetVolume, (P.Mode, "mute")));
        Assert.True(h.Volume.Muted);
    }

    [Fact]
    public async Task Conditions_WindowAndFile()
    {
        var h = new RunnerHarness();
        h.Windows.Add("Telegram", "telegram");
        var ctx = new RunContext();
        Assert.True(await h.Runner.EvaluateAsync(new StepCondition { Kind = ConditionKind.WindowExists, Parameters = { [P.App] = "телеграм" } }, ctx, default));
        Assert.False(await h.Runner.EvaluateAsync(new StepCondition { Kind = ConditionKind.WindowActive, Parameters = { [P.App] = "Telegram" } }, ctx, default));
        Assert.False(await h.Runner.EvaluateAsync(new StepCondition { Kind = ConditionKind.FileExists, Parameters = { [P.Path] = "/nonexistent/x" } }, ctx, default));
    }

    [Fact]
    public async Task FindText_And_ClickElement_UseVision()
    {
        var vision = new FakeVision();
        vision.VisibleTexts.Add("Отправить");
        var h = new RunnerHarness(vision);
        Assert.True((await h.Run(Steps.Of(StepKind.FindText, (P.Text, "Отправить")))).Success);
        Assert.False((await h.Run(Steps.Of(StepKind.FindText, (P.Text, "Нет такого")))).Success);
        Assert.True((await h.Run(Steps.Of(StepKind.ClickElement, (P.Element, "Отправить")))).Success);
        Assert.Equal(1, vision.Activations);
    }
}
