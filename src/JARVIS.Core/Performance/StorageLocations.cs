namespace Jarvis.Core.Performance;

public sealed record StorageResolution(string Path, bool UsedFallback, string? Message);

/// <summary>
/// Выбор места для необязательных файлов (экспорт, отчёты, архивы). Основные данные всегда
/// в %LOCALAPPDATA%\JARVIS. Если дополнительное место недоступно — используется локальная папка
/// с понятным сообщением; данные при этом не удаляются.
/// </summary>
public static class StorageLocations
{
    public static (bool Ok, string Message) Probe(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return (false, "Путь не задан");
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (root is null || !Directory.Exists(root)) return (false, $"Диск {root} недоступен");
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".jarvis-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return (true, "Доступно");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static StorageResolution Resolve(string? extraDir, string subFolder, string localFallback)
    {
        if (!string.IsNullOrWhiteSpace(extraDir))
        {
            var target = Path.Combine(extraDir, subFolder);
            var (ok, msg) = Probe(target);
            if (ok) return new StorageResolution(target, false, null);
            Directory.CreateDirectory(localFallback);
            return new StorageResolution(localFallback, true,
                $"Дополнительное хранилище недоступно ({msg}). Файл сохранён в локальную папку {localFallback}.");
        }
        Directory.CreateDirectory(localFallback);
        return new StorageResolution(localFallback, false, null);
    }
}
