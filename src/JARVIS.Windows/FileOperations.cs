using Jarvis.Core.Abstractions;
using static Jarvis.Platform.Win32;

namespace Jarvis.Platform;

public sealed class FileOperations : IFileOperations
{
    public bool FileExists(string path) => File.Exists(Expand(path));
    public bool DirectoryExists(string path) => Directory.Exists(Expand(path));

    public void CreateFile(string path, string content, bool overwrite)
    {
        path = Expand(path);
        if (File.Exists(path) && !overwrite) throw new IOException($"Файл уже существует: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, content);
    }

    public void Move(string source, string destination)
    {
        source = Expand(source);
        destination = Expand(destination);
        if (Directory.Exists(destination)) destination = Path.Combine(destination, Path.GetFileName(source));
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException($"По адресу назначения уже есть объект: {destination}");
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    /// <summary>Удаление только в корзину (с возможностью восстановления).</summary>
    public void DeleteToRecycleBin(string path)
    {
        path = Path.GetFullPath(Expand(path));
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Объект не найден", path);
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
        };
        var rc = SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted) throw new IOException($"Не удалось переместить в корзину (код {rc})");
    }

    private static string Expand(string p) => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
}
