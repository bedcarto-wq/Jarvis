namespace Jarvis.Core.Apps;

/// <summary>
/// Встроенная таблица распространённых программ и их произношений.
/// Записи объединяются с найденными на компьютере по имени процесса/названию.
/// </summary>
public static class BuiltInApps
{
    public sealed record Definition(
        string Id, string DisplayName, string[] Aliases, string[] ProcessNames, string[] NameHints,
        LaunchKind Kind = LaunchKind.Executable, string? Target = null, bool RelaunchShowsWindow = false);

    public static readonly IReadOnlyList<Definition> All =
    [
        new("chrome", "Google Chrome", ["chrome", "хром", "гугл хром", "google chrome", "хроме"], ["chrome"], ["google chrome", "chrome"]),
        new("claude", "Claude", ["claude", "клод", "клауд", "клоуд", "клод ай"], ["claude"], ["claude"], RelaunchShowsWindow: true),
        new("telegram", "Telegram", ["telegram", "телеграм", "телеграмм", "телега"], ["telegram"], ["telegram"], RelaunchShowsWindow: true),
        new("discord", "Discord", ["discord", "дискорд", "дискорт"], ["discord"], ["discord"], RelaunchShowsWindow: true),
        new("edge", "Microsoft Edge", ["edge", "эдж", "майкрософт эдж", "microsoft edge"], ["msedge"], ["microsoft edge"]),
        new("firefox", "Firefox", ["firefox", "файрфокс", "фаерфокс", "мозилла"], ["firefox"], ["firefox", "mozilla firefox"]),
        new("yandex", "Яндекс Браузер", ["яндекс браузер", "yandex browser", "яндекс"], ["browser"], ["yandex"]),
        new("opera", "Opera", ["opera", "опера"], ["opera"], ["opera"]),
        new("steam", "Steam", ["steam", "стим"], ["steam"], ["steam"], RelaunchShowsWindow: true),
        new("spotify", "Spotify", ["spotify", "спотифай"], ["spotify"], ["spotify"], RelaunchShowsWindow: true),
        new("vscode", "Visual Studio Code", ["vs code", "визуал студио код", "вс код", "vscode"], ["code"], ["visual studio code"]),
        new("word", "Microsoft Word", ["word", "ворд"], ["winword"], ["word"]),
        new("excel", "Microsoft Excel", ["excel", "эксель", "ексель"], ["excel"], ["excel"]),
        new("obs", "OBS Studio", ["obs", "обс"], ["obs64", "obs"], ["obs studio"]),
        new("notepad", "Блокнот", ["блокнот", "notepad"], ["notepad"], ["notepad", "блокнот"], LaunchKind.Executable, "notepad.exe"),
        new("explorer", "Проводник", ["проводник", "explorer", "файлы"], ["explorer"], ["проводник", "file explorer"], LaunchKind.Executable, "explorer.exe"),
        new("calc", "Калькулятор", ["калькулятор", "calculator"], ["calculatorapp", "calc", "applicationframehost"], ["калькулятор", "calculator"], LaunchKind.Executable, "calc.exe"),
        new("taskmgr", "Диспетчер задач", ["диспетчер задач", "task manager"], ["taskmgr"], ["диспетчер задач", "task manager"], LaunchKind.Executable, "taskmgr.exe"),
        new("paint", "Paint", ["paint", "пейнт", "паинт"], ["mspaint"], ["paint"], LaunchKind.Executable, "mspaint.exe"),
        new("settings", "Параметры Windows", ["параметры", "настройки windows", "параметры windows"], ["systemsettings"], ["параметры", "settings"], LaunchKind.Uri, "ms-settings:"),
        new("browser", "Браузер по умолчанию", ["браузер", "browser", "интернет"], [], [], LaunchKind.DefaultBrowser, "https://"),
    ];

    public static AppEntry ToEntry(Definition d) => new()
    {
        Id = d.Id,
        DisplayName = d.DisplayName,
        Aliases = d.Aliases.ToList(),
        ProcessNames = d.ProcessNames.ToList(),
        LaunchKind = d.Kind,
        LaunchTarget = d.Target,
        Source = AppSource.BuiltIn,
        RelaunchShowsWindow = d.RelaunchShowsWindow,
    };

    /// <summary>Подбирает встроенное описание для найденной программы.</summary>
    public static Definition? MatchDiscovered(string displayName, string? exePath)
    {
        var exe = exePath is null ? null : Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        var name = displayName.ToLowerInvariant();
        foreach (var d in All)
        {
            if (exe is not null && d.ProcessNames.Contains(exe) && d.Id != "calc") return d;
            if (d.NameHints.Any(h => name == h)) return d;
        }
        return null;
    }
}
