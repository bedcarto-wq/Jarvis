using Jarvis.Core.Abstractions;
using Jarvis.Core.Apps;
using Jarvis.Core.Vision;
using Jarvis.Templates;

namespace Jarvis.Automation;

/// <summary>
/// Алгоритм запуска с проверкой результата:
/// 1) уже запущена — переключиться; 2) системный запуск; 3) ожидание окна (до 5 с);
/// 4) до трёх попыток устранить ошибку (обновление базы, привязка-ярлык, повтор только
/// для однооконных программ); 5) просьба указать ярлык курсором.
/// </summary>
public sealed class AppLaunchCoordinator
{
    private readonly AutomationServices _s;
    public const int MaxAttempts = 3;

    public AppLaunchCoordinator(AutomationServices services) => _s = services;

    public async Task<StepOutcome> LaunchAsync(AppEntry app, bool newInstance, CancellationToken ct)
    {
        if (!newInstance)
        {
            var existing = WindowMatcher.FindWindows(_s.Windows.GetTopLevelWindows(), app);
            if (existing.Count > 0)
            {
                return await ActivateAndVerifyAsync(existing[0].Handle, ct)
                    ? StepOutcome.Ok($"{app.DisplayName} уже запущен — окно активировано")
                    : StepOutcome.Fail($"{app.DisplayName} запущен, но окно не удалось активировать");
            }
        }

        bool launchedOnce = false;
        string lastError = "окно не появилось";
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            bool running = app.ProcessNames.Count > 0 && _s.Windows.GetProcessIds(app.ProcessNames).Count > 0;
            // Не запускаем вслепую повторно, если процесс уже работает: дубликаты недопустимы.
            // Исключения: процесс завершился (ошибка запуска) или однооконная программа,
            // которая при повторном запуске лишь показывает своё окно.
            bool shouldStart = !launchedOnce
                ? !running || app.RelaunchShowsWindow || newInstance
                : !running || (app.RelaunchShowsWindow && attempt == 2 && !newInstance);
            if (shouldStart)
            {
                var r = await StartAsync(app, ct);
                if (!r.Started)
                {
                    lastError = r.Error ?? "не удалось запустить";
                    _s.Log.Warn($"Запуск {app.DisplayName}, попытка {attempt}: {lastError}");
                    var refreshed = await RefreshEntryAsync(app, ct);
                    if (refreshed is not null) app = refreshed;
                    continue;
                }
                launchedOnce = true;
            }

            var window = await WaitForWindowAsync(app, _s.Settings().LaunchTimeoutMs, ct);
            if (window is not null)
            {
                await ActivateAndVerifyAsync(window.Handle, ct);
                return StepOutcome.Ok(attempt == 1 ? $"{app.DisplayName} запущен" : $"{app.DisplayName} запущен с попытки {attempt}");
            }
            // Медленный ПК: процесс уже работает, но окно ещё не нарисовано — ждём ещё, а не считаем программу неработающей.
            if (app.ProcessNames.Count > 0 && _s.Windows.GetProcessIds(app.ProcessNames).Count > 0)
            {
                _s.Log.Info($"{app.DisplayName}: процесс запущен, окно ещё не появилось — жду дольше.");
                window = await WaitForWindowAsync(app, _s.Settings().LaunchTimeoutMs, ct);
                if (window is not null)
                {
                    await ActivateAndVerifyAsync(window.Handle, ct);
                    return StepOutcome.Ok($"{app.DisplayName} запущен (медленный запуск)");
                }
                lastError = "процесс запущен, но окно так и не появилось";
                break;
            }
            lastError = "окно не появилось за отведённое время";
            var updated = await RefreshEntryAsync(app, ct);
            if (updated is not null) app = updated;
        }

        // Просим помощи пользователя: указать ярлык/кнопку запуска.
        return await AskUserToPointAsync(app, lastError, ct);
    }

    private async Task<LaunchStartResult> StartAsync(AppEntry app, CancellationToken ct)
    {
        if (app.LaunchTarget is null && app.LaunchKind != LaunchKind.DefaultBrowser && app.LaunchAnchorId is { } anchorId && _s.Vision is not null)
            return await StartViaAnchorAsync(anchorId, ct);
        var r = _s.Launcher.Start(app);
        if (!r.Started && app.LaunchAnchorId is { } id && _s.Vision is not null)
            return await StartViaAnchorAsync(id, ct);
        return r;
    }

    private async Task<LaunchStartResult> StartViaAnchorAsync(Guid anchorId, CancellationToken ct)
    {
        var loc = await _s.Vision!.LocateAsync(new ElementQuery { AnchorId = anchorId }, ct);
        if (!loc.Found) return new LaunchStartResult(false, null, "запомненный ярлык не найден");
        var ok = await _s.Vision.ActivateElementAsync(loc, ClickKind.Double, ct);
        return new LaunchStartResult(ok, null, ok ? null : "не удалось нажать запомненный ярлык");
    }

    /// <summary>Повторный анализ: обновление базы программ, если запись устарела.</summary>
    private async Task<AppEntry?> RefreshEntryAsync(AppEntry app, CancellationToken ct)
    {
        if (_s.Discovery is null) return null;
        try
        {
            var found = await _s.Discovery.DiscoverAsync(ct);
            _s.Catalog.Merge(found);
            return _s.Catalog.Get(app.Id) ?? AppResolver.Resolve(app.DisplayName, _s.Catalog);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _s.Log.Warn("Обновление базы программ: " + ex.Message);
            return null;
        }
    }

    public async Task<WindowInfo?> WaitForWindowAsync(AppEntry app, int timeoutMs, CancellationToken ct)
    {
        int waited = 0;
        while (true)
        {
            var w = WindowMatcher.FindWindows(_s.Windows.GetTopLevelWindows(), app);
            if (w.Count > 0) return w[0];
            if (waited >= timeoutMs) return null;
            var poll = Math.Clamp(_s.Settings().Performance.WindowPollMs, 100, 2000);
            await _s.Delay.Delay(poll, ct);
            waited += poll;
        }
    }

    public async Task<bool> ActivateAndVerifyAsync(nint handle, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            var info = _s.Windows.GetWindow(handle);
            if (info is { IsMinimized: true }) _s.Windows.Restore(handle);
            _s.Windows.Activate(handle);
            for (int k = 0; k < 4; k++)
            {
                await _s.Delay.Delay(100, ct);
                if (_s.Windows.GetForegroundWindow()?.Handle == handle) return true;
            }
        }
        return false;
    }

    private async Task<StepOutcome> AskUserToPointAsync(AppEntry app, string lastError, CancellationToken ct)
    {
        if (_s.Vision is null)
            return StepOutcome.Fail($"Не удалось запустить {app.DisplayName}: {lastError}", retryable: false);
        var point = await _s.Ui.RequestPointAsync(
            $"Не удалось запустить {app.DisplayName}. Наведите курсор на ярлык программы и скажите «здесь».", ct);
        if (point is null)
            return StepOutcome.Fail($"Не удалось запустить {app.DisplayName}: {lastError}", retryable: false);

        var element = _s.Vision.GetElementAt(point.Value);
        var anchor = element is not null
            ? AnchorStore.FromElement(element, point.Value, $"Ярлык {app.DisplayName}", app.Id)
            : new ElementAnchor { Label = $"Ярлык {app.DisplayName}", AppId = app.Id, ScreenX = point.Value.X, ScreenY = point.Value.Y };
        _s.Input.MoveMouse(point.Value);
        _s.Input.Click(MouseButton.Left, 2);
        var window = await WaitForWindowAsync(app, _s.Settings().LaunchTimeoutMs, ct);
        if (window is null)
            return StepOutcome.Fail($"{app.DisplayName} не запустился и после нажатия на указанный элемент", retryable: false);
        _s.Anchors.Upsert(anchor);
        app.LaunchAnchorId = anchor.Id;
        _s.Catalog.Upsert(app);
        await ActivateAndVerifyAsync(window.Handle, ct);
        return StepOutcome.Ok($"{app.DisplayName} запущен. Ярлык запомнен");
    }
}
