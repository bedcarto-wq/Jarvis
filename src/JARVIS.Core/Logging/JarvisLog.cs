namespace Jarvis.Core.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} [{Level}] {Message}";
}

public interface IJarvisLog
{
    void Write(LogLevel level, string message);
    void Debug(string message) => Write(LogLevel.Debug, message);
    void Info(string message) => Write(LogLevel.Info, message);
    void Warn(string message) => Write(LogLevel.Warning, message);
    void Error(string message) => Write(LogLevel.Error, message);
}

/// <summary>
/// Журнал в памяти (кольцевой буфер). На диск пишет только в режиме отладки,
/// чтобы не вести историю активности по умолчанию.
/// </summary>
public sealed class JarvisLog : IJarvisLog
{
    private readonly object _lock = new();
    private readonly LinkedList<LogEntry> _entries = new();
    private const int Capacity = 500;

    public event Action<LogEntry>? EntryAdded;
    public bool DebugEnabled { get; set; }
    public string? DebugFileDirectory { get; set; }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    public void Write(LogLevel level, string message)
    {
        if (level == LogLevel.Debug && !DebugEnabled) return;
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > Capacity) _entries.RemoveFirst();
            if (DebugEnabled && DebugFileDirectory is not null)
            {
                try
                {
                    Directory.CreateDirectory(DebugFileDirectory);
                    File.AppendAllText(Path.Combine(DebugFileDirectory, $"jarvis-{DateTime.Now:yyyyMMdd}.log"),
                        entry + Environment.NewLine);
                }
                catch { /* журнал не должен ронять приложение */ }
            }
        }
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock) return _entries.ToList();
    }
}

public sealed class NullLog : IJarvisLog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string message) { }
}
