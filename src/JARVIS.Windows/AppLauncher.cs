using System.Diagnostics;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Apps;

namespace Jarvis.Platform;

public sealed class AppLauncher : IAppLauncher
{
    public LaunchStartResult Start(AppEntry app)
    {
        try
        {
            ProcessStartInfo psi;
            switch (app.LaunchKind)
            {
                case LaunchKind.Executable:
                    var target = Environment.ExpandEnvironmentVariables(app.LaunchTarget ?? "");
                    if (target.Length == 0) return new(false, null, "Не указан путь к программе");
                    if (Path.IsPathRooted(target) && !File.Exists(target)) return new(false, null, $"Файл не найден: {target}");
                    psi = new ProcessStartInfo(target, app.Arguments ?? "")
                    {
                        UseShellExecute = true,
                        WorkingDirectory = app.WorkingDirectory ?? (Path.IsPathRooted(target) ? Path.GetDirectoryName(target) ?? "" : ""),
                    };
                    break;
                case LaunchKind.Shortcut:
                    if (app.LaunchTarget is null || !File.Exists(app.LaunchTarget)) return new(false, null, "Ярлык не найден");
                    psi = new ProcessStartInfo(app.LaunchTarget) { UseShellExecute = true };
                    break;
                case LaunchKind.AppUserModelId:
                    psi = new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.LaunchTarget}") { UseShellExecute = true };
                    break;
                case LaunchKind.Uri:
                    var uri = app.LaunchTarget ?? "";
                    if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme is "file" or "javascript")
                        return new(false, null, "Недопустимый адрес запуска");
                    psi = new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true };
                    break;
                case LaunchKind.DefaultBrowser:
                    psi = new ProcessStartInfo(app.LaunchTarget ?? "https://www.google.com") { UseShellExecute = true };
                    break;
                default:
                    return new(false, null, "Неизвестный способ запуска");
            }
            using var p = Process.Start(psi);
            int? pid = null;
            try { if (p is not null && !p.HasExited) pid = p.Id; } catch { /* процесс-посредник */ }
            return new(true, pid, null);
        }
        catch (Exception ex)
        {
            return new(false, null, ex.Message);
        }
    }
}
