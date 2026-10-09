using System.Diagnostics;
using Jarvis.Core.Abstractions;
using static Jarvis.Platform.Win32;

namespace Jarvis.Platform;

/// <summary>Работа с окнами через Win32. Каждое действие проверяется по фактическому состоянию.</summary>
public sealed class WindowService : IWindowService
{
    private readonly Dictionary<int, string> _procNames = new();
    private readonly int _ownPid = Environment.ProcessId;

    private IReadOnlyList<WindowInfo>? _cache;
    private long _cacheAt;

    /// <summary>Сколько миллисекунд можно повторно использовать список окон (параметр профиля).</summary>
    public Func<int> CacheMs { get; set; } = () => 0;

    /// <summary>Сбрасывает кэш после действий с окнами.</summary>
    public void Invalidate() => _cache = null;

    public IReadOnlyList<WindowInfo> GetTopLevelWindows()
    {
        var ttl = CacheMs();
        var c = _cache;
        if (ttl > 0 && c is not null && Environment.TickCount64 - Volatile.Read(ref _cacheAt) < ttl) return c;
        var fresh = EnumerateTopLevel();
        _cache = fresh;
        Volatile.Write(ref _cacheAt, Environment.TickCount64);
        return fresh;
    }

    private IReadOnlyList<WindowInfo> EnumerateTopLevel()
    {
        var fg = Win32.GetForegroundWindow();
        var list = new List<WindowInfo>();
        lock (_procNames) _procNames.Clear();
        EnumWindows((h, _) =>
        {
            if (IsAppWindow(h)) { var w = Describe(h, fg); if (w is not null) list.Add(w); }
            return true;
        }, 0);
        return list;
    }

    private bool IsAppWindow(nint h)
    {
        if (!IsWindowVisible(h)) return false;
        if (GetWindowTextLength(h) == 0) return false;
        var ex = GetWindowLong(h, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (Win32.GetWindow(h, GW_OWNER) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        GetWindowThreadProcessId(h, out var pid);
        return pid != _ownPid;
    }

    private WindowInfo? Describe(nint h, nint fg)
    {
        if (!IsWindow(h)) return null;
        GetWindowThreadProcessId(h, out var pid);
        GetWindowRect(h, out var r);
        return new WindowInfo(h, GetText(h), ProcessName(pid), pid,
            new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), IsIconic(h), IsZoomed(h), h == fg);
    }

    private string ProcessName(int pid)
    {
        lock (_procNames)
        {
            if (_procNames.TryGetValue(pid, out var n)) return n;
            try { using var p = Process.GetProcessById(pid); n = p.ProcessName; }
            catch { n = ""; }
            _procNames[pid] = n;
            return n;
        }
    }

    public WindowInfo? GetForegroundWindow()
    {
        var fg = Win32.GetForegroundWindow();
        return fg == 0 ? null : Describe(fg, fg);
    }

    public WindowInfo? GetWindow(nint handle) => Describe(handle, Win32.GetForegroundWindow());

    public bool Activate(nint handle)
    {
        Invalidate();
        if (!IsWindow(handle)) return false;
        if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
        if (Win32.GetForegroundWindow() == handle) return true;
        // Windows ограничивает перехват фокуса; используем стандартный приём с AttachThreadInput
        // и «пустым» нажатием Alt, чтобы система разрешила смену переднего окна.
        var fg = Win32.GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var myThread = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        try
        {
            TapAlt();
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            ShowWindow(handle, IsZoomed(handle) ? SW_MAXIMIZE : SW_SHOW);
        }
        finally
        {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
        for (var i = 0; i < 10; i++)
        {
            if (Win32.GetForegroundWindow() == handle) return true;
            Thread.Sleep(30);
        }
        return Win32.GetForegroundWindow() == handle;
    }

    private static void TapAlt()
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0x12 } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0x12, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    public bool Minimize(nint handle)
    {
        Invalidate();
        if (!IsWindow(handle)) return false;
        ShowWindow(handle, SW_MINIMIZE);
        return WaitFor(() => IsIconic(handle));
    }

    public bool Maximize(nint handle)
    {
        Invalidate();
        if (!IsWindow(handle)) return false;
        ShowWindow(handle, SW_MAXIMIZE);
        return WaitFor(() => IsZoomed(handle));
    }

    public bool Restore(nint handle)
    {
        Invalidate();
        if (!IsWindow(handle)) return false;
        ShowWindow(handle, SW_RESTORE);
        return WaitFor(() => !IsIconic(handle));
    }

    public bool Close(nint handle)
    {
        Invalidate();
        if (!IsWindow(handle)) return true;
        PostMessage(handle, WM_CLOSE, 0, 0);
        // Приложение может спросить «Сохранить?» — тогда окно останется, и это не ошибка JARVIS.
        return WaitFor(() => !IsWindow(handle) || !IsWindowVisible(handle), 3000);
    }

    public void ShowDesktop()
    {
        Invalidate();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is not null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                shell.ToggleDesktop();
                return;
            }
        }
        catch { /* запасной вариант ниже */ }
        foreach (var w in GetTopLevelWindows()) ShowWindowAsync(w.Handle, SW_SHOWMINIMIZED);
    }

    public IReadOnlyList<int> GetProcessIds(IEnumerable<string> processNames)
    {
        var ids = new List<int>();
        foreach (var name in processNames.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var n = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            foreach (var p in Process.GetProcessesByName(n)) { ids.Add(p.Id); p.Dispose(); }
        }
        return ids;
    }

    private static bool WaitFor(Func<bool> cond, int timeoutMs = 1000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(40);
        }
        return cond();
    }
}
