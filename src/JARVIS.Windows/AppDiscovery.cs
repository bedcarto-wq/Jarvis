using System.Diagnostics;
using System.Text.Json;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Apps;
using Jarvis.Core.Logging;
using Microsoft.Win32;

namespace Jarvis.Platform;

/// <summary>
/// Поиск установленных программ: ярлыки меню «Пуск» (общие и пользовательские),
/// реестр App Paths и приложения Microsoft Store (Get-StartApps).
/// </summary>
public sealed class AppDiscovery(IJarvisLog? log = null, Func<int>? parallelism = null) : IAppDiscovery
{
    private readonly IJarvisLog _log = log ?? NullLog.Instance;
    private readonly Func<int> _parallelism = parallelism ?? (() => 1);

    private static readonly string[] SkipWords =
        ["uninstall", "удаление", "удалить", "readme", "help", "справка", "documentation", "license", "лицензия", "website", "release notes"];

    public async Task<IReadOnlyList<AppEntry>> DiscoverAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        if (_parallelism() > 1)
        {
            // Параллельно (профиль разрешает >1 фоновой задачи): отдельные словари, затем слияние.
            var a = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            var b = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            await Task.WhenAll(Task.Run(() => ScanStartMenu(a, ct), ct), Task.Run(() => ScanAppPaths(b, ct), ct));
            foreach (var kv in a) result[kv.Key] = kv.Value;
            foreach (var kv in b) result.TryAdd(kv.Key, kv.Value);
        }
        else
        {
            await Task.Run(() => ScanStartMenu(result, ct), ct);
            await Task.Run(() => ScanAppPaths(result, ct), ct);
        }
        try { await ScanStoreAppsAsync(result, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Warn($"Get-StartApps: {ex.Message}"); }
        _log.Info($"Найдено программ: {result.Count}");
        return result.Values.ToList();
    }

    private void ScanStartMenu(Dictionary<string, AppEntry> result, CancellationToken ct)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch (Exception ex) { _log.Warn($"Меню «Пуск»: {ex.Message}"); continue; }
            foreach (var lnk in files)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (SkipWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                var info = ShellLink.Read(lnk);
                var target = info?.Target;
                if (target is not null && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                {
                    var exeName = Path.GetFileNameWithoutExtension(target);
                    if (exeName.Contains("unins", StringComparison.OrdinalIgnoreCase)) continue;
                    Add(result, new AppEntry
                    {
                        DisplayName = name,
                        LaunchKind = LaunchKind.Executable,
                        LaunchTarget = target,
                        Arguments = info!.Arguments,
                        WorkingDirectory = info.WorkingDirectory,
                        ProcessNames = [exeName],
                        Source = AppSource.StartMenu,
                    });
                }
                else if (target is null || !target.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                {
                    // «Объявленные» ярлыки установщиков MSI: запускаем сам ярлык.
                    Add(result, new AppEntry
                    {
                        DisplayName = name,
                        LaunchKind = LaunchKind.Shortcut,
                        LaunchTarget = lnk,
                        ProcessNames = target is null ? [] : [Path.GetFileNameWithoutExtension(target)],
                        Source = AppSource.StartMenu,
                    });
                }
            }
        }
    }

    private void ScanAppPaths(Dictionary<string, AppEntry> result, CancellationToken ct)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var k = key.OpenSubKey(sub);
                if (k?.GetValue(null) is not string path) continue;
                path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
                if (!File.Exists(path)) continue;
                var exe = Path.GetFileNameWithoutExtension(path);
                if (result.Values.Any(a => a.ProcessNames.Contains(exe, StringComparer.OrdinalIgnoreCase))) continue;
                string display;
                try { display = FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d ? d : exe; }
                catch { display = exe; }
                Add(result, new AppEntry
                {
                    DisplayName = display,
                    LaunchKind = LaunchKind.Executable,
                    LaunchTarget = path,
                    ProcessNames = [exe],
                    Source = AppSource.AppPaths,
                });
            }
        }
    }

    private async Task ScanStoreAppsAsync(Dictionary<string, AppEntry> result, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi);
        if (p is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var json = await p.StandardOutput.ReadToEndAsync(timeout.Token);
        await p.WaitForExitAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(json)) return;
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var it in items)
        {
            var name = it.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var id = it.TryGetProperty("AppID", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || !id.Contains('!')) continue;
            if (SkipWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            if (result.ContainsKey(name)) continue;
            Add(result, new AppEntry
            {
                DisplayName = name,
                LaunchKind = LaunchKind.AppUserModelId,
                LaunchTarget = id,
                Source = AppSource.Store,
            });
        }
    }

    private static void Add(Dictionary<string, AppEntry> result, AppEntry e)
    {
        e.Id = AppCatalog.MakeId(e.DisplayName);
        if (e.Id.Length == 0) return;
        if (!result.ContainsKey(e.DisplayName)) result[e.DisplayName] = e;
    }
}
