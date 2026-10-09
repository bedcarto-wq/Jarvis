using Jarvis.Core;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Apps;
using Jarvis.Core.Execution;
using Jarvis.Core.Logging;
using Jarvis.Core.Settings;
using Jarvis.Core.Vision;

namespace Jarvis.Automation;

/// <summary>Набор сервисов, нужных исполнителю действий.</summary>
public sealed class AutomationServices
{
    public required IWindowService Windows { get; init; }
    public required IAppLauncher Launcher { get; init; }
    public IAppDiscovery? Discovery { get; init; }
    public required AppCatalog Catalog { get; init; }
    public required IInputService Input { get; init; }
    public IVisionService? Vision { get; init; }
    public required IFileOperations Files { get; init; }
    public IVolumeService? Volume { get; init; }
    public required ISystemOperations System { get; init; }
    public ISpeechOutput? Speech { get; init; }
    public required IUserInteraction Ui { get; init; }
    public required AnchorStore Anchors { get; init; }
    public required UndoJournal Undo { get; init; }
    public required Func<JarvisSettings> Settings { get; init; }
    public required AppPaths Paths { get; init; }
    public IDelay Delay { get; init; } = RealDelay.Instance;
    public IJarvisLog Log { get; init; } = NullLog.Instance;
}

public static class AppResolver
{
    /// <summary>Находит программу по Id, названию, псевдониму или пути к exe.</summary>
    public static AppEntry? Resolve(string? nameOrId, AppCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;
        var byId = catalog.Get(nameOrId);
        if (byId is not null) return byId;
        var exact = catalog.All.FirstOrDefault(a => string.Equals(a.DisplayName, nameOrId, StringComparison.CurrentCultureIgnoreCase));
        if (exact is not null) return exact;
        if (nameOrId.Contains('\\') || nameOrId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new AppEntry
            {
                Id = AppCatalog.MakeId(Path.GetFileNameWithoutExtension(nameOrId)),
                DisplayName = Path.GetFileNameWithoutExtension(nameOrId),
                LaunchKind = LaunchKind.Executable,
                LaunchTarget = Environment.ExpandEnvironmentVariables(nameOrId),
                ProcessNames = [Path.GetFileNameWithoutExtension(nameOrId)],
                Source = AppSource.User,
            };
        }
        var m = AppMatcher.Match(nameOrId, catalog.All);
        return m.Found ? m.Best!.App : null;
    }
}
