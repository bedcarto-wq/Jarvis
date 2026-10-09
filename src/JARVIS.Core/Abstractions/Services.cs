using Jarvis.Core.Apps;
using Jarvis.Core.Security;

namespace Jarvis.Core.Abstractions;

public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    ScreenRect Bounds,
    bool IsMinimized,
    bool IsMaximized,
    bool IsForeground);

/// <summary>Управление окнами верхнего уровня (Win32).</summary>
public interface IWindowService
{
    IReadOnlyList<WindowInfo> GetTopLevelWindows();
    WindowInfo? GetForegroundWindow();
    WindowInfo? GetWindow(nint handle);
    bool Activate(nint handle);
    bool Minimize(nint handle);
    bool Maximize(nint handle);
    bool Restore(nint handle);
    bool Close(nint handle);
    void ShowDesktop();
    IReadOnlyList<int> GetProcessIds(IEnumerable<string> processNames);
}

public sealed record LaunchStartResult(bool Started, int? ProcessId, string? Error);

public interface IAppLauncher
{
    LaunchStartResult Start(AppEntry app);
}

public interface IAppDiscovery
{
    Task<IReadOnlyList<AppEntry>> DiscoverAsync(CancellationToken ct);
}

public enum MouseButton { Left, Right, Middle }

/// <summary>Мышь и клавиатура (SendInput). Обязан уметь отпускать все удерживаемые клавиши.</summary>
public interface IInputService
{
    ScreenPoint GetCursorPosition();
    void MoveMouse(ScreenPoint p);
    void Click(MouseButton button, int count = 1);
    void MouseDown(MouseButton button);
    void MouseUp(MouseButton button);
    Task DragAsync(ScreenPoint from, ScreenPoint to, CancellationToken ct);
    void KeyDown(VirtualKey key);
    void KeyUp(VirtualKey key);
    Task PressChordAsync(KeyChord chord, int holdMs, CancellationToken ct);
    Task TypeTextAsync(string text, int delayMs, CancellationToken ct);
    /// <summary>Аварийно отпускает все клавиши и кнопки мыши, нажатые JARVIS.</summary>
    void ReleaseAll();
}

public interface IFileOperations
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateFile(string path, string content, bool overwrite);
    void Move(string source, string destination);
    /// <summary>Удаляет в корзину (поддерживает восстановление).</summary>
    void DeleteToRecycleBin(string path);
}

public interface IVolumeService
{
    int GetVolume();
    void SetVolume(int percent);
    void ChangeVolume(int deltaPercent);
    void SetMute(bool mute);
    bool IsMuted();
}

public enum SystemOperation
{
    Screenshot, LockScreen, Sleep, Shutdown, Restart, ShowDesktop, OpenSettings, OpenTaskManager, EmptyRecycleBin,
}

public interface ISystemOperations
{
    void OpenUrl(Uri uri);
    string TakeScreenshot(string directory, nint? window = null);
    void Execute(SystemOperation op, string? argument = null);
}

public interface ISpeechOutput
{
    bool IsAvailable { get; }
    string StatusMessage { get; }
    void Say(string text);
    void Cancel();
}

public interface IDelay
{
    Task Delay(int milliseconds, CancellationToken ct);
}

public sealed class RealDelay : IDelay
{
    public static readonly RealDelay Instance = new();
    public Task Delay(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);
}

public enum NotifyLevel { Info, Success, Warning, Error }

/// <summary>Взаимодействие с пользователем: подтверждения, уточнения, просьба указать элемент.</summary>
public interface IUserInteraction
{
    Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken ct);
    Task<ScreenPoint?> RequestPointAsync(string message, CancellationToken ct);
    Task<int?> ChooseAsync(string question, IReadOnlyList<string> options, CancellationToken ct);
    void Notify(string message, NotifyLevel level = NotifyLevel.Info);
}
