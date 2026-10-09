using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Jarvis.Core.Abstractions;
using static Jarvis.Platform.Win32;

namespace Jarvis.Platform;

public sealed class SystemOperations : ISystemOperations
{
    public void OpenUrl(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Разрешены только адреса http/https");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    public string TakeScreenshot(string directory, nint? window = null)
    {
        Directory.CreateDirectory(directory);
        Rectangle rect;
        if (window is { } h && h != 0 && GetWindowRect(h, out var r))
            rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        else
            rect = new Rectangle(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
        using var bmp = ScreenCapture.Capture(rect);
        var path = Path.Combine(directory, $"JARVIS_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    public void Execute(SystemOperation op, string? argument = null)
    {
        switch (op)
        {
            case SystemOperation.Screenshot: TakeScreenshot(argument ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)); break;
            case SystemOperation.LockScreen: LockWorkStation(); break;
            case SystemOperation.Sleep: SetSuspendState(false, false, false); break;
            case SystemOperation.Shutdown: Run("shutdown.exe", "/s /t 5"); break;
            case SystemOperation.Restart: Run("shutdown.exe", "/r /t 5"); break;
            case SystemOperation.ShowDesktop: new WindowService().ShowDesktop(); break;
            case SystemOperation.OpenSettings: Shell(string.IsNullOrWhiteSpace(argument) ? "ms-settings:" : argument!); break;
            case SystemOperation.OpenTaskManager: Run("taskmgr.exe", ""); break;
            case SystemOperation.EmptyRecycleBin: SHEmptyRecycleBin(0, null, 0x1 | 0x2 | 0x4); break;
        }
    }

    private static void Run(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, CreateNoWindow = true })?.Dispose();

    private static void Shell(string target)
    {
        if (!target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Разрешены только адреса ms-settings:");
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }
}

public static class ScreenCapture
{
    public static Bitmap Capture(Rectangle rect)
    {
        var bmp = new Bitmap(Math.Max(1, rect.Width), Math.Max(1, rect.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static Rectangle VirtualScreen() => new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
}
