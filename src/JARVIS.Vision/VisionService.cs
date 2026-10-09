using System.IO;
using System.Diagnostics;
using System.Windows.Automation;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Logging;
using Jarvis.Core.Text;
using Jarvis.Core.Vision;
using Jarvis.Platform;

namespace Jarvis.Vision;

/// <summary>
/// «Зрение» JARVIS. Порядок поиска элемента: запомненная привязка → UI Automation
/// (AutomationId, затем имя) → OCR по тексту → относительные координаты → абсолютные.
/// </summary>
public sealed class VisionService : IVisionService, IDisposable
{
    private readonly IWindowService _windows;
    private readonly IInputService _input;
    private readonly AnchorStore _anchors;
    private readonly Func<TesseractOcr?> _ocr;
    private readonly IJarvisLog _log;
    private readonly Func<Jarvis.Core.Performance.PerformanceSettings> _tuning;

    public VisionService(IWindowService windows, IInputService input, AnchorStore anchors, Func<TesseractOcr?> ocr, IJarvisLog? log = null,
        Func<Jarvis.Core.Performance.PerformanceSettings>? tuning = null)
    {
        _tuning = tuning ?? (() => new Jarvis.Core.Performance.PerformanceSettings());
        _windows = windows;
        _input = input;
        _anchors = anchors;
        _ocr = ocr;
        _log = log ?? NullLog.Instance;
    }

    private int OcrWindowLimit => Math.Clamp(_tuning().OcrMaxWindows, 1, 5);

    public bool OcrAvailable => _ocr()?.Available == true;
    public string OcrStatus => _ocr()?.Status ?? "OCR отключён в настройках";

    public UiElementInfo? GetElementAt(ScreenPoint point)
    {
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            return el is null ? null : Describe(el);
        }
        catch (Exception ex)
        {
            _log.Debug($"UIA FromPoint: {ex.Message}");
            return null;
        }
    }

    private UiElementInfo Describe(AutomationElement el)
    {
        var c = el.Current;
        var r = c.BoundingRectangle;
        var bounds = r.IsEmpty ? default : new ScreenRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        string? proc = null;
        try { using var p = Process.GetProcessById(c.ProcessId); proc = p.ProcessName; } catch { }
        var top = TopWindow(el);
        ScreenRect? wb = null;
        string? title = null;
        if (top is not null)
        {
            var tr = top.Current.BoundingRectangle;
            if (!tr.IsEmpty) wb = new ScreenRect((int)tr.X, (int)tr.Y, (int)tr.Width, (int)tr.Height);
            title = top.Current.Name;
        }
        var invoke = el.TryGetCurrentPattern(InvokePattern.Pattern, out _);
        return new UiElementInfo(NullIfEmpty(c.Name), NullIfEmpty(c.AutomationId), c.ControlType?.ProgrammaticName?.Replace("ControlType.", ""),
            NullIfEmpty(c.ClassName), bounds, proc, title, wb, invoke);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static AutomationElement? TopWindow(AutomationElement el)
    {
        var walker = TreeWalker.ControlViewWalker;
        var root = AutomationElement.RootElement;
        AutomationElement? cur = el, last = null;
        for (var i = 0; i < 50 && cur is not null && !Automation.Compare(cur, root); i++)
        {
            last = cur;
            cur = walker.GetParent(cur);
        }
        return last;
    }

    public async Task<LocateResult> LocateAsync(ElementQuery q, CancellationToken ct)
    {
        return await Task.Run(() => Locate(q, ct), ct);
    }

    private LocateResult Locate(ElementQuery q, CancellationToken ct)
    {
        // 1. Запомненная привязка.
        if (q.AnchorId is { } id)
        {
            var a = _anchors.Get(id);
            if (a is null) return LocateResult.NotFound with { Detail = "Привязка не найдена" };
            var r = LocateAnchor(a, q, ct);
            a.LastUsedAt = DateTime.Now;
            if (r.Found) a.SuccessCount++; else a.FailureCount++;
            try { _anchors.Upsert(a); } catch { }
            return r;
        }
        var windows = TargetWindows(q.Window, q.ProcessNames);
        // 2–3. UI Automation.
        foreach (var w in windows)
        {
            ct.ThrowIfCancellationRequested();
            var r = FindUia(w.Handle, q.AutomationId, q.Name ?? q.Text, q.ControlType, ct);
            if (r.Found) return r;
        }
        // 4. OCR по видимому тексту.
        var text = q.Text ?? q.Name;
        if (text is not null && q.AllowOcr)
        {
            foreach (var w in windows.Take(OcrWindowLimit))
            {
                var r = FindByOcr(text, w.Bounds, ct);
                if (r.Found) return r;
            }
        }
        return LocateResult.NotFound with { Detail = $"Элемент «{q}» не найден" };
    }

    private LocateResult LocateAnchor(ElementAnchor a, ElementQuery q, CancellationToken ct)
    {
        var procs = a.ProcessName is null ? q.ProcessNames : [a.ProcessName];
        var windows = TargetWindows(q.Window, procs);
        if (a.ProcessName is not null && windows.Count == 0 && procs.Count > 0)
        {
            // Привязка к рабочему столу/панели задач: окно процесса может отсутствовать.
            windows = [];
        }
        foreach (var w in windows)
        {
            var r = FindUia(w.Handle, a.AutomationId, a.ElementName, a.ControlType, ct);
            if (r.Found) return r with { Method = LocateMethod.Anchor, Detail = "Привязка (UI Automation)" };
        }
        if (a.VisibleText is not null && q.AllowOcr)
        {
            foreach (var w in windows.Take(OcrWindowLimit))
            {
                var r = FindByOcr(a.VisibleText, w.Bounds, ct);
                if (r.Found) return r with { Method = LocateMethod.Anchor, Detail = "Привязка (OCR)" };
            }
        }
        if (!q.AllowCoordinates) return LocateResult.NotFound with { Detail = "Привязка не найдена без координат" };
        var win = windows.FirstOrDefault();
        if (win is not null && a.RelativeX is { } rx && a.RelativeY is { } ry && !win.IsMinimized)
        {
            var x = win.Bounds.X + (int)(rx * win.Bounds.Width);
            var y = win.Bounds.Y + (int)(ry * win.Bounds.Height);
            return new LocateResult(true, LocateMethod.RelativeCoordinates, new ScreenRect(x - a.Width / 2, y - a.Height / 2, Math.Max(1, a.Width), Math.Max(1, a.Height)), null,
                "Относительные координаты в окне (может быть неточно)");
        }
        if (a.ScreenX is { } sx && a.ScreenY is { } sy)
            return new LocateResult(true, LocateMethod.AbsoluteCoordinates, new ScreenRect(sx - a.Width / 2, sy - a.Height / 2, Math.Max(1, a.Width), Math.Max(1, a.Height)), null,
                "Абсолютные координаты (последний вариант, может быть неточно)");
        return LocateResult.NotFound with { Detail = "Привязку не удалось найти" };
    }

    private List<WindowInfo> TargetWindows(nint? window, IReadOnlyList<string> processNames)
    {
        if (window is { } h && h != 0 && _windows.GetWindow(h) is { } w) return [w];
        if (processNames.Count > 0)
        {
            var set = processNames.Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = _windows.GetTopLevelWindows().Where(x => set.Contains(x.ProcessName))
                .OrderByDescending(x => x.IsForeground).ToList();
            if (found.Count > 0) return found;
            return [];
        }
        var fg = _windows.GetForegroundWindow();
        return fg is null ? [] : [fg];
    }

    private LocateResult FindUia(nint hwnd, string? automationId, string? name, string? controlType, CancellationToken ct)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return LocateResult.NotFound;
            if (!string.IsNullOrWhiteSpace(automationId))
            {
                var el = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                if (el is not null && Usable(el)) return Found(el, LocateMethod.UiAutomationId, $"UI Automation: AutomationId={automationId}");
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                var el = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, name, PropertyConditionFlags.IgnoreCase));
                if (el is not null && Usable(el)) return Found(el, LocateMethod.UiAutomationName, $"UI Automation: «{el.Current.Name}»");
                var fuzzy = WalkForName(root, name, controlType, ct);
                if (fuzzy is not null) return Found(fuzzy, LocateMethod.UiAutomationName, $"UI Automation (похожее имя): «{fuzzy.Current.Name}»");
            }
        }
        catch (ElementNotAvailableException) { }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Debug($"UIA: {ex.Message}"); }
        return LocateResult.NotFound;
    }

    private static bool Usable(AutomationElement el)
    {
        try { return !el.Current.IsOffscreen && !el.Current.BoundingRectangle.IsEmpty; }
        catch { return false; }
    }

    private AutomationElement? WalkForName(AutomationElement root, string name, string? controlType, CancellationToken ct)
    {
        var tuning = _tuning();
        var maxWalk = Math.Clamp(tuning.UiaMaxElements, 200, 10000);
        var maxMs = Math.Clamp(tuning.UiaWalkTimeMs, 500, 10000);
        var target = TextNormalizer.Normalize(name);
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        AutomationElement? best = null;
        double bestScore = 0;
        var visited = 0;
        var sw = Stopwatch.StartNew();
        while (queue.Count > 0 && visited < maxWalk && sw.ElapsedMilliseconds < maxMs)
        {
            ct.ThrowIfCancellationRequested();
            var el = queue.Dequeue();
            visited++;
            try
            {
                var n = TextNormalizer.Normalize(el.Current.Name);
                if (n.Length > 0 && Usable(el))
                {
                    var score = n == target ? 1.0 : n.Contains(target) || target.Contains(n) && n.Length > 2 ? 0.85 : Fuzzy.Similarity(n, target);
                    if (controlType is not null && el.Current.ControlType.ProgrammaticName.EndsWith(controlType, StringComparison.OrdinalIgnoreCase)) score += 0.05;
                    if (score > bestScore) { bestScore = score; best = el; }
                    if (score >= 1) break;
                }
                for (var c = walker.GetFirstChild(el); c is not null; c = walker.GetNextSibling(c)) queue.Enqueue(c);
            }
            catch (ElementNotAvailableException) { }
        }
        return bestScore >= 0.8 ? best : null;
    }

    private static LocateResult Found(AutomationElement el, LocateMethod method, string detail)
    {
        var r = el.Current.BoundingRectangle;
        return new LocateResult(true, method, new ScreenRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height), el, detail);
    }

    private LocateResult FindByOcr(string text, ScreenRect region, CancellationToken ct)
    {
        var ocr = _ocr();
        if (ocr is null || !ocr.Available) return LocateResult.NotFound;
        var words = ocr.Read(region, ct);
        var target = TextNormalizer.Words(TextNormalizer.Normalize(text));
        if (target.Length == 0) return LocateResult.NotFound;
        var norm = words.Select(w => (W: w, N: TextNormalizer.Normalize(w.Text))).ToList();
        for (var i = 0; i < norm.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < target.Length && ok; j++)
                ok = i + j < norm.Count && Fuzzy.Similarity(norm[i + j].N, target[j]) >= 0.75;
            if (!ok) continue;
            var first = norm[i].W.Bounds;
            var last = norm[i + target.Length - 1].W.Bounds;
            var rect = new ScreenRect(first.X, Math.Min(first.Y, last.Y), last.X + last.Width - first.X, Math.Max(first.Height, last.Height));
            return new LocateResult(true, LocateMethod.Ocr, rect, null, $"OCR: «{string.Join(' ', norm.Skip(i).Take(target.Length).Select(x => x.W.Text))}»");
        }
        return LocateResult.NotFound;
    }

    public async Task<bool> ActivateElementAsync(LocateResult located, ClickKind click, CancellationToken ct)
    {
        if (!located.Found) return false;
        if (click == ClickKind.Single && located.NativeElement is AutomationElement el)
        {
            try
            {
                if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) { ((InvokePattern)p).Invoke(); return true; }
                if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var t)) { ((TogglePattern)t).Toggle(); return true; }
                if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var s)) { ((SelectionItemPattern)s).Select(); return true; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException)
            {
                _log.Debug($"UIA-паттерн не сработал, нажимаю мышью: {ex.Message}");
            }
        }
        var center = located.Bounds.Center;
        _input.MoveMouse(center);
        await Task.Delay(60, ct);
        switch (click)
        {
            case ClickKind.Double: _input.Click(MouseButton.Left, 2); break;
            case ClickKind.Right: _input.Click(MouseButton.Right); break;
            default: _input.Click(MouseButton.Left); break;
        }
        return true;
    }

    public async Task<LocateResult> FindTextAsync(string text, nint? window, bool allowOcr, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var wins = TargetWindows(window, []);
            foreach (var w in wins)
            {
                var r = FindUia(w.Handle, null, text, null, ct);
                if (r.Found) return r;
            }
            if (allowOcr)
            {
                foreach (var w in wins.Take(OcrWindowLimit))
                {
                    var r = FindByOcr(text, w.Bounds, ct);
                    if (r.Found) return r;
                }
            }
            return LocateResult.NotFound with { Detail = $"Текст «{text}» не найден" };
        }, ct);
    }

    public async Task<IReadOnlyList<UiElementInfo>> SnapshotElementsAsync(nint window, int maxElements, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var list = new List<UiElementInfo>();
            try
            {
                var root = AutomationElement.FromHandle(window);
                var walker = TreeWalker.ControlViewWalker;
                var queue = new Queue<AutomationElement>();
                queue.Enqueue(root);
                while (queue.Count > 0 && list.Count < maxElements)
                {
                    ct.ThrowIfCancellationRequested();
                    var el = queue.Dequeue();
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(el.Current.Name) && Usable(el)) list.Add(Describe(el));
                        for (var c = walker.GetFirstChild(el); c is not null; c = walker.GetNextSibling(c)) queue.Enqueue(c);
                    }
                    catch (ElementNotAvailableException) { }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.Debug($"UIA snapshot: {ex.Message}"); }
            return (IReadOnlyList<UiElementInfo>)list;
        }, ct);
    }

    public void Dispose() => _ocr()?.Dispose();
}
