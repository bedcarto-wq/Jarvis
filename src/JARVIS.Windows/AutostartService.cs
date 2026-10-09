using Microsoft.Win32;

namespace Jarvis.Platform;

/// <summary>Автозапуск через HKCU\...\Run (без прав администратора).</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JARVIS";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --tray");
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, false);
    }
}
