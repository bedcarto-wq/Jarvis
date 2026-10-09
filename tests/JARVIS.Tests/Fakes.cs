using Jarvis.Automation;
using Jarvis.Core;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Apps;
using Jarvis.Core.Execution;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Jarvis.Core.Vision;
using Jarvis.Templates;

namespace Jarvis.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jarvis-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public sealed class FakeDelay : IDelay
{
    public int TotalMs;
    public async Task Delay(int milliseconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TotalMs += milliseconds;
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }
}

public sealed class FakeWindows : IWindowService
{
    private nint _next = 100;
    public List<WindowInfo> Windows { get; } = [];
    public HashSet<string> BackgroundProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public nint Foreground;
    public List<nint> Closed { get; } = [];
    public bool IgnoreClose;

    public WindowInfo Add(string title, string process)
    {
        var w = new WindowInfo(_next++, title, process, 1000 + (int)_next, new ScreenRect(0, 0, 800, 600), false, false, false);
        Windows.Add(w);
        return w;
    }

    public IReadOnlyList<WindowInfo> GetTopLevelWindows() =>
        Windows.Select(w => w with { IsForeground = w.Handle == Foreground }).ToList();
    public WindowInfo? GetForegroundWindow() => GetTopLevelWindows().FirstOrDefault(w => w.Handle == Foreground);
    public WindowInfo? GetWindow(nint handle) => GetTopLevelWindows().FirstOrDefault(w => w.Handle == handle);
    public bool Activate(nint handle) { Foreground = handle; return true; }
    public bool Minimize(nint handle) => Update(handle, w => w with { IsMinimized = true, IsMaximized = false });
    public bool Maximize(nint handle) => Update(handle, w => w with { IsMaximized = true, IsMinimized = false });
    public bool Restore(nint handle) => Update(handle, w => w with { IsMaximized = false, IsMinimized = false });
    public bool Close(nint handle)
    {
        Closed.Add(handle);
        if (!IgnoreClose) Windows.RemoveAll(w => w.Handle == handle);
        return true;
    }
    public void ShowDesktop() { }
    public IReadOnlyList<int> GetProcessIds(IEnumerable<string> processNames)
    {
        var set = processNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = Windows.Where(w => set.Contains(w.ProcessName)).Select(w => w.ProcessId).ToList();
        if (BackgroundProcesses.Overlaps(set)) ids.Add(1);
        return ids;
    }

    private bool Update(nint h, Func<WindowInfo, WindowInfo> f)
    {
        var i = Windows.FindIndex(w => w.Handle == h);
        if (i < 0) return false;
        Windows[i] = f(Windows[i]);
        return true;
    }
}

public sealed class FakeLauncher(FakeWindows windows) : IAppLauncher
{
    public int Starts;
    public bool Fail;
    public bool CreateWindow = true;
    public LaunchStartResult Start(AppEntry app)
    {
        Starts++;
        if (Fail) return new LaunchStartResult(false, null, "файл не найден");
        if (CreateWindow) windows.Add(app.DisplayName, app.ProcessNames.FirstOrDefault() ?? app.Id);
        return new LaunchStartResult(true, 42, null);
    }
}

public sealed class FakeInput : IInputService
{
    public List<string> Log { get; } = [];
    public int ReleaseAllCount;
    public ScreenPoint GetCursorPosition() => new(0, 0);
    public void MoveMouse(ScreenPoint p) => Log.Add($"move {p.X},{p.Y}");
    public void Click(MouseButton button, int count = 1) => Log.Add($"click {button} x{count}");
    public void MouseDown(MouseButton button) => Log.Add("down");
    public void MouseUp(MouseButton button) => Log.Add("up");
    public Task DragAsync(ScreenPoint from, ScreenPoint to, CancellationToken ct) { Log.Add("drag"); return Task.CompletedTask; }
    public void KeyDown(VirtualKey key) => Log.Add($"down {key}");
    public void KeyUp(VirtualKey key) => Log.Add($"up {key}");
    public Task PressChordAsync(KeyChord chord, int holdMs, CancellationToken ct) { Log.Add($"chord {chord}"); return Task.CompletedTask; }
    public Task TypeTextAsync(string text, int delayMs, CancellationToken ct) { Log.Add($"type {text}"); return Task.CompletedTask; }
    public void ReleaseAll() => ReleaseAllCount++;
}

public sealed class FakeFiles : IFileOperations
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Recycled { get; } = [];
    public bool FileExists(string path) => Files.ContainsKey(path);
    public bool DirectoryExists(string path) => false;
    public void CreateFile(string path, string content, bool overwrite) => Files[path] = content;
    public void Move(string source, string destination)
    {
        Files[destination] = Files[source];
        Files.Remove(source);
    }
    public void DeleteToRecycleBin(string path) { Files.Remove(path); Recycled.Add(path); }
}

public sealed class FakeSystem : ISystemOperations
{
    public List<string> Calls { get; } = [];
    public void OpenUrl(Uri uri) => Calls.Add("url " + uri);
    public string TakeScreenshot(string directory, nint? window = null) { Calls.Add("screenshot"); return Path.Combine(directory, "s.png"); }
    public void Execute(SystemOperation op, string? argument = null) => Calls.Add(op.ToString());
}

public sealed class FakeVolume : IVolumeService
{
    public int Level = 50; public bool Muted;
    public int GetVolume() => Level;
    public void SetVolume(int percent) => Level = Math.Clamp(percent, 0, 100);
    public void ChangeVolume(int deltaPercent) => SetVolume(Level + deltaPercent);
    public void SetMute(bool mute) => Muted = mute;
    public bool IsMuted() => Muted;
}

public sealed class FakeUi : IUserInteraction
{
    public bool ConfirmResult = true;
    public bool HangOnConfirm;
    public int ConfirmCalls;
    public int PointRequests;
    public ScreenPoint? PointResult;
    public List<string> Notifications { get; } = [];
    public TaskCompletionSource ConfirmStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken ct)
    {
        ConfirmCalls++;
        ConfirmStarted.TrySetResult();
        if (HangOnConfirm) await Task.Delay(Timeout.Infinite, ct);
        return ConfirmResult;
    }
    public Task<ScreenPoint?> RequestPointAsync(string message, CancellationToken ct) { PointRequests++; return Task.FromResult(PointResult); }
    public Task<int?> ChooseAsync(string question, IReadOnlyList<string> options, CancellationToken ct) => Task.FromResult<int?>(null);
    public void Notify(string message, NotifyLevel level = NotifyLevel.Info) => Notifications.Add(message);
}

/// <summary>Исполнитель-заглушка для тестов интерпретатора: записывает шаги, результаты задаются.</summary>
public sealed class RecordingRunner : IStepActionRunner
{
    public List<string> Executed { get; } = [];
    public Func<TemplateStep, StepOutcome> Outcome { get; set; } = _ => StepOutcome.Ok();
    public Func<StepCondition, bool> Condition { get; set; } = _ => true;
    public Func<TemplateStep, CancellationToken, Task>? BeforeExecute { get; set; }

    public async Task<StepOutcome> ExecuteAsync(TemplateStep step, RunContext ctx, CancellationToken ct)
    {
        if (BeforeExecute is not null) await BeforeExecute(step, ct);
        Executed.Add(step.Get(P.Text) ?? step.Get(P.App) ?? step.Kind.ToString());
        return Outcome(step);
    }

    public Task<bool> EvaluateAsync(StepCondition condition, RunContext ctx, CancellationToken ct) =>
        Task.FromResult(Condition(condition));
}

public sealed class InMemoryRepo : ITemplateRepository
{
    public List<Template> Items { get; } = [];
    public IReadOnlyList<Template> All => Items;
    public Template? Get(Guid id) => Items.FirstOrDefault(t => t.Id == id);
    public Template? FindByName(string name) => Items.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}

public static class Steps
{
    public static TemplateStep Say(string text) => StepCatalog.Create(StepKind.Say).Set(P.Text, text);
    public static TemplateStep Of(StepKind k, params (string, string)[] p)
    {
        var s = StepCatalog.Create(k);
        foreach (var (key, v) in p) s.Parameters[key] = v;
        return s;
    }
}

public sealed class ExecutorHarness
{
    public JarvisSettings Settings { get; } = new() { StepDelayMs = 0 };
    public InMemoryRepo Repo { get; } = new();
    public RecordingRunner Runner { get; } = new();
    public FakeUi Ui { get; } = new();
    public FakeInput Input { get; } = new();
    public FakeDelay Delay { get; } = new();
    public StopController Stop { get; }
    public TemplateExecutor Executor { get; }

    public ExecutorHarness()
    {
        Stop = new StopController(Input);
        Executor = new TemplateExecutor(Repo, Runner, new SecurityPolicy(() => Settings), Ui, Stop, () => Settings, Delay);
    }

    public Template Add(string name, params TemplateStep[] steps)
    {
        var t = new Template { Name = name, Steps = steps.ToList() };
        Repo.Items.Add(t);
        return t;
    }
}

public sealed class RunnerHarness
{
    public JarvisSettings Settings { get; } = new() { StepDelayMs = 0 };
    public FakeWindows Windows { get; } = new();
    public FakeLauncher Launcher { get; }
    public FakeInput Input { get; } = new();
    public FakeFiles Files { get; } = new();
    public FakeSystem System { get; } = new();
    public FakeVolume Volume { get; } = new();
    public FakeUi Ui { get; } = new();
    public AppCatalog Catalog { get; } = new();
    public UndoJournal Undo { get; } = new();
    public ActionRunner Runner { get; }

    public RunnerHarness(IVisionService? vision = null)
    {
        Launcher = new FakeLauncher(Windows);
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "jarvis-runner-" + Guid.NewGuid().ToString("N")));
        Runner = new ActionRunner(new AutomationServices
        {
            Windows = Windows, Launcher = Launcher, Catalog = Catalog, Input = Input, Files = Files,
            Volume = Volume, System = System, Ui = Ui, Anchors = new AnchorStore(Path.Combine(paths.Root, "a.json")),
            Undo = Undo, Settings = () => Settings, Paths = paths, Delay = new FakeDelay(), Vision = vision,
        });
    }

    public Task<StepOutcome> Run(TemplateStep step) => Runner.ExecuteAsync(step, new RunContext(), CancellationToken.None);
}
