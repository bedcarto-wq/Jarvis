namespace Jarvis.Core.Apps;

public enum LaunchKind { Executable, Shortcut, AppUserModelId, Uri, DefaultBrowser }

public enum AppSource { BuiltIn, StartMenu, AppPaths, Store, RunningWindow, User }

public sealed class AppEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Псевдонимы для распознавания (русские и английские произношения).</summary>
    public List<string> Aliases { get; set; } = [];
    public LaunchKind LaunchKind { get; set; } = LaunchKind.Executable;
    public string? LaunchTarget { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    /// <summary>Имена процессов без «.exe» для поиска окон.</summary>
    public List<string> ProcessNames { get; set; } = [];
    public AppSource Source { get; set; }
    /// <summary>Пользователь правил запись — повторное сканирование её не перезаписывает.</summary>
    public bool UserEdited { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Однооконные приложения (Telegram, Discord) показывают окно при повторном запуске.</summary>
    public bool RelaunchShowsWindow { get; set; }
    /// <summary>Привязка к ярлыку/кнопке запуска, указанная пользователем.</summary>
    public Guid? LaunchAnchorId { get; set; }

    public IEnumerable<string> AllNames()
    {
        yield return DisplayName;
        foreach (var a in Aliases) yield return a;
        foreach (var p in ProcessNames) yield return p;
    }

    public override string ToString() => DisplayName;
}
