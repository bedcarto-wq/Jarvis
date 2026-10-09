using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Jarvis.Core.Storage;

public enum LoadStatus { Ok, Missing, Corrupt, NewerSchema }

public sealed record LoadResult<T>(T Value, LoadStatus Status, string? BackupPath = null, string? Error = null);

/// <summary>Документ с версией схемы, в который заворачиваются все сохраняемые файлы.</summary>
public sealed class VersionedDocument<T>
{
    public int SchemaVersion { get; set; }
    public T? Data { get; set; }
}

/// <summary>
/// Чтение/запись JSON: атомарная запись через временный файл,
/// повреждённые файлы переносятся в резервную копию, а не удаляются.
/// </summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static LoadResult<T> Load<T>(string path, int currentSchema, Func<T> factory, string? corruptDir = null)
        where T : class
    {
        if (!File.Exists(path))
            return new LoadResult<T>(factory(), LoadStatus.Missing);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new LoadResult<T>(factory(), LoadStatus.Corrupt, null, ex.Message);
        }
        return Parse(text, path, currentSchema, factory, corruptDir);
    }

    public static LoadResult<T> Parse<T>(string text, string? sourcePath, int currentSchema, Func<T> factory, string? corruptDir = null)
        where T : class
    {
        try
        {
            var doc = JsonSerializer.Deserialize<VersionedDocument<T>>(text, Options);
            if (doc?.Data is null)
                throw new JsonException("Документ не содержит данных (Data).");
            if (doc.SchemaVersion > currentSchema)
            {
                // Файл от более новой версии: читаем, но не перезаписываем молча.
                return new LoadResult<T>(doc.Data, LoadStatus.NewerSchema, null,
                    $"Версия схемы {doc.SchemaVersion} новее поддерживаемой {currentSchema}.");
            }
            return new LoadResult<T>(doc.Data, LoadStatus.Ok);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            string? backup = sourcePath is null ? null : BackupCorrupt(sourcePath, corruptDir);
            return new LoadResult<T>(factory(), LoadStatus.Corrupt, backup, ex.Message);
        }
    }

    public static void Save<T>(string path, int schemaVersion, T data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var doc = new VersionedDocument<T> { SchemaVersion = schemaVersion, Data = data };
        var json = JsonSerializer.Serialize(doc, Options);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public static string Serialize<T>(int schemaVersion, T data) =>
        JsonSerializer.Serialize(new VersionedDocument<T> { SchemaVersion = schemaVersion, Data = data }, Options);

    public static T? DeepClone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options);

    private static string? BackupCorrupt(string path, string? corruptDir)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var dir = corruptDir ?? Path.GetDirectoryName(path) ?? ".";
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir,
                $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}{Path.GetExtension(path)}");
            File.Move(path, target, overwrite: true);
            return target;
        }
        catch
        {
            return null;
        }
    }
}
