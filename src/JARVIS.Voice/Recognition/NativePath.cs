using System.Runtime.InteropServices;
using System.Text;

namespace Jarvis.Voice.Recognition;

/// <summary>
/// Нативные библиотеки распознавания принимают пути как ANSI/UTF-8 по-разному.
/// Если путь содержит не-ASCII символы (например, кириллическое имя пользователя Windows),
/// на Windows подставляется короткое имя 8.3, состоящее только из ASCII.
/// </summary>
internal static partial class NativePath
{
    public static string Safe(string path)
    {
        if (!OperatingSystem.IsWindows() || path.All(c => c < 128)) return path;
        var sb = new StringBuilder(1024);
        var n = GetShortPathNameW(path, sb, sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, int bufferSize);
}
