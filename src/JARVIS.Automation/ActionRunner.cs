using Jarvis.Core;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Apps;
using Jarvis.Core.Commands;
using Jarvis.Core.Execution;
using Jarvis.Core.Vision;
using Jarvis.Templates;

namespace Jarvis.Automation;

/// <summary>Исполняет отдельные блоки шаблонов через системные сервисы.</summary>
public sealed class ActionRunner : IStepActionRunner
{
    private readonly AutomationServices _s;
    private readonly AppLaunchCoordinator _launcher;

    public ActionRunner(AutomationServices services)
    {
        _s = services;
        _launcher = new AppLaunchCoordinator(services);
    }

    public AppLaunchCoordinator Launcher => _launcher;

    public async Task<StepOutcome> ExecuteAsync(TemplateStep step, RunContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (step.Kind)
        {
            case StepKind.LaunchApp:
            {
                var app = AppResolver.Resolve(step.Get(P.App), _s.Catalog);
                if (app is null) return StepOutcome.Fail($"Программа «{step.Get(P.App)}» не найдена в базе", retryable: false);
                return await _launcher.LaunchAsync(app, step.GetBool(P.NewInstance), ct);
            }
            case StepKind.SwitchToWindow:
                return await SwitchAsync(step, ct);
            case StepKind.CloseWindow:
                return await CloseAsync(step, ct);
            case StepKind.MinimizeWindow:
            case StepKind.MaximizeWindow:
                return await MinMaxAsync(step, ct);
            case StepKind.WaitForApp:
            {
                var app = AppResolver.Resolve(step.Get(P.App), _s.Catalog);
                if (app is null) return StepOutcome.Fail($"Программа «{step.Get(P.App)}» не найдена в базе", false);
                var w = await _launcher.WaitForWindowAsync(app, Math.Clamp(step.GetInt(P.TimeoutMs, 10000), 0, 600_000), ct);
                return w is null ? StepOutcome.Fail($"Окно {app.DisplayName} не появилось", false) : StepOutcome.Ok($"Окно {app.DisplayName} найдено");
            }
            case StepKind.WaitForElement:
                return await WaitForElementAsync(step, ctx, ct);
            case StepKind.FindText:
            {
                if (_s.Vision is null) return StepOutcome.Fail("Модуль зрения недоступен", false);
                var text = step.Get(P.Text)!;
                var loc = await _s.Vision.FindTextAsync(text, WindowFor(step.Get(P.App)), _s.Settings().VisionUseOcr, ct);
                if (!loc.Found) return StepOutcome.Fail($"Текст «{text}» не найден");
                ctx.LastFound = loc.Bounds;
                return StepOutcome.Ok($"Текст найден ({loc.Method})");
            }
            case StepKind.ClickElement:
            {
                if (_s.Vision is null) return StepOutcome.Fail("Модуль зрения недоступен", false);
                var q = BuildQuery(step);
                var loc = await _s.Vision.LocateAsync(q, ct);
                if (!loc.Found) return StepOutcome.Fail($"Элемент «{q}» не найден");
                var click = step.Get(P.Click) switch { "double" => ClickKind.Double, "right" => ClickKind.Right, _ => ClickKind.Single };
                ctx.LastFound = loc.Bounds;
                return await _s.Vision.ActivateElementAsync(loc, click, ct)
                    ? StepOutcome.Ok($"Нажато ({loc.Method})")
                    : StepOutcome.Fail("Не удалось нажать элемент");
            }
            case StepKind.Mouse:
                return await MouseAsync(step, ctx, ct);
            case StepKind.TypeText:
                await _s.Input.TypeTextAsync(step.Get(P.Text) ?? "", _s.Settings().TypingDelayMs, ct);
                return StepOutcome.Ok("Текст введён");
            case StepKind.PressKeys:
            {
                if (!KeyChord.TryParse(step.Get(P.Keys), out var chord))
                    return StepOutcome.Fail($"Неверное сочетание «{step.Get(P.Keys)}»", false);
                await _s.Input.PressChordAsync(chord, Math.Clamp(step.GetInt(P.HoldMs, 0), 0, 10_000), ct);
                return StepOutcome.Ok($"Нажато {chord}");
            }
            case StepKind.OpenUrl:
            {
                var raw = step.Get(P.Url) ?? "";
                var url = raw.Contains("://") ? raw : CommandParser.NormalizeUrl(raw);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return StepOutcome.Fail($"Недопустимый адрес «{raw}» (разрешены только http и https)", false);
                _s.System.OpenUrl(uri);
                return StepOutcome.Ok($"Открыт {uri.Host}");
            }
            case StepKind.CreateFile:
                return CreateFile(step);
            case StepKind.MoveFile:
                return MoveFile(step);
            case StepKind.DeleteFile:
            {
                var path = FullPath(step.Get(P.Path));
                if (path is null || (!_s.Files.FileExists(path) && !_s.Files.DirectoryExists(path)))
                    return StepOutcome.Fail($"Файл не найден: {step.Get(P.Path)}", false);
                _s.Files.DeleteToRecycleBin(path);
                return StepOutcome.Ok("Перемещено в корзину");
            }
            case StepKind.SetVolume:
                return SetVolume(step);
            case StepKind.SystemOperation:
            {
                if (!Enum.TryParse<SystemOperation>(step.Get(P.Operation), true, out var op))
                    return StepOutcome.Fail($"Неизвестная операция «{step.Get(P.Operation)}»", false);
                if (op == SystemOperation.Screenshot)
                {
                    var file = _s.System.TakeScreenshot(_s.Paths.ScreenshotsDir);
                    return StepOutcome.Ok("Снимок сохранён: " + file);
                }
                _s.System.Execute(op, step.Get(P.Argument));
                return StepOutcome.Ok("Выполнено");
            }
            case StepKind.Say:
                _s.Speech?.Say(step.Get(P.Text) ?? "");
                return StepOutcome.Ok();
            default:
                return StepOutcome.Fail($"Блок {step.Kind} не исполняется напрямую", false);
        }
    }

    public async Task<bool> EvaluateAsync(StepCondition c, RunContext ctx, CancellationToken ct)
    {
        var p = c.Parameters;
        string? Get(string k) => p.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        switch (c.Kind)
        {
            case ConditionKind.WindowExists:
                return FindWindows(Get(P.App), Get(P.Title)).Count > 0;
            case ConditionKind.WindowActive:
            {
                var fg = _s.Windows.GetForegroundWindow();
                return fg is not null && FindWindows(Get(P.App), Get(P.Title)).Any(w => w.Handle == fg.Handle);
            }
            case ConditionKind.TextOnScreen:
            {
                if (_s.Vision is null || Get(P.Text) is not { } text) return false;
                var r = await _s.Vision.FindTextAsync(text, WindowFor(Get(P.App)), _s.Settings().VisionUseOcr, ct);
                if (r.Found) ctx.LastFound = r.Bounds;
                return r.Found;
            }
            case ConditionKind.ElementExists:
            {
                if (_s.Vision is null) return false;
                var step = new TemplateStep { Parameters = new Dictionary<string, string>(p) };
                var r = await _s.Vision.LocateAsync(BuildQuery(step, allowCoordinates: false), ct);
                return r.Found;
            }
            case ConditionKind.FileExists:
            {
                var path = FullPath(Get(P.Path));
                return path is not null && (_s.Files.FileExists(path) || _s.Files.DirectoryExists(path));
            }
            case ConditionKind.PreviousStepSucceeded:
                return ctx.LastSuccess;
            default:
                return false;
        }
    }

    private IReadOnlyList<WindowInfo> FindWindows(string? app, string? title)
    {
        var all = _s.Windows.GetTopLevelWindows();
        if (app is not null)
        {
            var entry = AppResolver.Resolve(app, _s.Catalog);
            return entry is null ? [] : WindowMatcher.FindWindows(all, entry);
        }
        return title is null ? [] : WindowMatcher.FindByTitle(all, title);
    }

    private nint? WindowFor(string? app)
    {
        if (app is null) return _s.Windows.GetForegroundWindow()?.Handle;
        var w = FindWindows(app, null);
        return w.Count > 0 ? w[0].Handle : null;
    }

    private async Task<StepOutcome> SwitchAsync(TemplateStep step, CancellationToken ct)
    {
        var appName = step.Get(P.App);
        var windows = FindWindows(appName, step.Get(P.Title));
        if (windows.Count == 0)
        {
            if (appName is not null && step.GetBool(P.LaunchIfMissing))
            {
                var app = AppResolver.Resolve(appName, _s.Catalog);
                if (app is not null) return await _launcher.LaunchAsync(app, false, ct);
            }
            return StepOutcome.Fail($"Окно «{appName ?? step.Get(P.Title)}» не найдено");
        }
        return await _launcher.ActivateAndVerifyAsync(windows[0].Handle, ct)
            ? StepOutcome.Ok($"Переключено на «{windows[0].Title}»")
            : StepOutcome.Fail("Windows не дала активировать окно");
    }

    private async Task<StepOutcome> CloseAsync(TemplateStep step, CancellationToken ct)
    {
        var appName = step.Get(P.App);
        IReadOnlyList<WindowInfo> targets = appName is null
            ? (_s.Windows.GetForegroundWindow() is { } fg ? [fg] : [])
            : FindWindows(appName, null);
        if (targets.Count == 0) return StepOutcome.Fail($"Окно «{appName ?? "активное"}» не найдено", false);
        foreach (var w in targets) _s.Windows.Close(w.Handle);
        var handles = targets.Select(t => t.Handle).ToHashSet();
        for (int i = 0; i < 12; i++)
        {
            await _s.Delay.Delay(250, ct);
            if (!_s.Windows.GetTopLevelWindows().Any(w => handles.Contains(w.Handle)))
                return StepOutcome.Ok("Закрыто");
        }
        return StepOutcome.Fail("Окно не закрылось — возможно, программа просит подтверждение", false);
    }

    private async Task<StepOutcome> MinMaxAsync(TemplateStep step, CancellationToken ct)
    {
        var appName = step.Get(P.App);
        var w = appName is null ? _s.Windows.GetForegroundWindow() : FindWindows(appName, null).FirstOrDefault();
        if (w is null) return StepOutcome.Fail($"Окно «{appName ?? "активное"}» не найдено", false);
        bool minimize = step.Kind == StepKind.MinimizeWindow;
        if (minimize) _s.Windows.Minimize(w.Handle); else _s.Windows.Maximize(w.Handle);
        await _s.Delay.Delay(200, ct);
        var after = _s.Windows.GetWindow(w.Handle);
        bool ok = after is not null && (minimize ? after.IsMinimized : after.IsMaximized);
        return ok ? StepOutcome.Ok(minimize ? "Свёрнуто" : "Развёрнуто") : StepOutcome.Fail("Состояние окна не изменилось");
    }

    private async Task<StepOutcome> WaitForElementAsync(TemplateStep step, RunContext ctx, CancellationToken ct)
    {
        if (_s.Vision is null) return StepOutcome.Fail("Модуль зрения недоступен", false);
        var perf = _s.Settings().Performance;
        int timeout = Math.Clamp(step.GetInt(P.TimeoutMs, perf.ElementWaitTimeoutMs), 0, 600_000);
        int poll = Math.Clamp(perf.ElementPollMs, 200, 3000);
        int ocrEvery = Math.Max(1, 2000 / poll);
        int waited = 0, iteration = 0;
        while (true)
        {
            // OCR дорогой — используем его не чаще раза в 2 секунды.
            bool ocr = _s.Settings().VisionUseOcr && iteration % ocrEvery == ocrEvery - 1;
            var q = BuildQuery(step, allowCoordinates: false, allowOcr: ocr);
            var r = await _s.Vision.LocateAsync(q, ct);
            if (r.Found)
            {
                ctx.LastFound = r.Bounds;
                return StepOutcome.Ok($"Элемент найден ({r.Method})");
            }
            if (waited >= timeout) return StepOutcome.Fail($"Элемент «{q}» не появился", false);
            await _s.Delay.Delay(poll, ct);
            waited += poll;
            iteration++;
        }
    }

    private async Task<StepOutcome> MouseAsync(TemplateStep step, RunContext ctx, CancellationToken ct)
    {
        int x = step.GetInt(P.X, 0), y = step.GetInt(P.Y, 0);
        int x2 = step.GetInt(P.X2, x), y2 = step.GetInt(P.Y2, y);
        if (step.Get(P.Relative) != "screen")
        {
            var fg = _s.Windows.GetForegroundWindow();
            if (fg is null) return StepOutcome.Fail("Нет активного окна для относительных координат");
            x += fg.Bounds.X; y += fg.Bounds.Y; x2 += fg.Bounds.X; y2 += fg.Bounds.Y;
        }
        var p1 = new ScreenPoint(x, y);
        switch (step.Get(P.Action))
        {
            case "move": _s.Input.MoveMouse(p1); break;
            case "double": _s.Input.MoveMouse(p1); _s.Input.Click(MouseButton.Left, 2); break;
            case "right": _s.Input.MoveMouse(p1); _s.Input.Click(MouseButton.Right); break;
            case "drag": await _s.Input.DragAsync(p1, new ScreenPoint(x2, y2), ct); break;
            default: _s.Input.MoveMouse(p1); _s.Input.Click(MouseButton.Left); break;
        }
        return StepOutcome.Ok();
    }

    private StepOutcome CreateFile(TemplateStep step)
    {
        var path = FullPath(step.Get(P.Path));
        if (path is null) return StepOutcome.Fail("Не указан путь", false);
        bool existed = _s.Files.FileExists(path);
        if (existed && !step.GetBool(P.Overwrite)) return StepOutcome.Fail($"Файл уже существует: {path}", false);
        _s.Files.CreateFile(path, step.Get(P.Content) ?? "", step.GetBool(P.Overwrite));
        if (!existed)
            _s.Undo.Push(new UndoEntry($"создание файла {Path.GetFileName(path)}", () => _s.Files.DeleteToRecycleBin(path)));
        return StepOutcome.Ok("Файл создан");
    }

    private StepOutcome MoveFile(TemplateStep step)
    {
        var src = FullPath(step.Get(P.Source));
        var dst = FullPath(step.Get(P.Destination));
        if (src is null || dst is null) return StepOutcome.Fail("Не указан путь", false);
        if (!_s.Files.FileExists(src) && !_s.Files.DirectoryExists(src)) return StepOutcome.Fail($"Не найден: {src}", false);
        // Если назначение — существующая папка, перемещаем внутрь неё.
        if (_s.Files.DirectoryExists(dst)) dst = Path.Combine(dst, Path.GetFileName(src));
        if (_s.Files.FileExists(dst) || _s.Files.DirectoryExists(dst)) return StepOutcome.Fail($"Уже существует: {dst}", false);
        _s.Files.Move(src, dst);
        var from = src; var to = dst;
        _s.Undo.Push(new UndoEntry($"перемещение {Path.GetFileName(from)}", () => _s.Files.Move(to, from)));
        return StepOutcome.Ok("Перемещено");
    }

    private StepOutcome SetVolume(TemplateStep step)
    {
        if (_s.Volume is null) return StepOutcome.Fail("Управление громкостью недоступно", false);
        int value = Math.Clamp(step.GetInt(P.Value, 10), 0, 100);
        switch (step.Get(P.Mode))
        {
            case "up": _s.Volume.SetMute(false); _s.Volume.ChangeVolume(value); break;
            case "down": _s.Volume.ChangeVolume(-value); break;
            case "mute": _s.Volume.SetMute(true); break;
            case "unmute": _s.Volume.SetMute(false); break;
            default: _s.Volume.SetMute(false); _s.Volume.SetVolume(value); break;
        }
        return StepOutcome.Ok($"Громкость {_s.Volume.GetVolume()}%");
    }

    private ElementQuery BuildQuery(TemplateStep step, bool allowCoordinates = true, bool? allowOcr = null)
    {
        var appName = step.Get(P.App);
        var app = appName is null ? null : AppResolver.Resolve(appName, _s.Catalog);
        Guid? anchor = Guid.TryParse(step.Get(P.Anchor), out var g) ? g : null;
        if (anchor is null && step.Get(P.Anchor) is { } label)
            anchor = _s.Anchors.FindByLabel(label, app?.Id)?.Id;
        return new ElementQuery
        {
            AppId = app?.Id,
            ProcessNames = app?.ProcessNames ?? [],
            Window = WindowFor(appName),
            Name = step.Get(P.Element),
            Text = step.Get(P.Element),
            AutomationId = step.Get(P.AutomationId),
            AnchorId = anchor,
            AllowOcr = allowOcr ?? _s.Settings().VisionUseOcr,
            AllowCoordinates = allowCoordinates && _s.Settings().VisionAllowCoordinateFallback,
        };
    }

    private static string? FullPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))); }
        catch { return null; }
    }
}
