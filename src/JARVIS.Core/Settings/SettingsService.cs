using Jarvis.Core.Storage;

namespace Jarvis.Core.Settings;

public sealed class SettingsService
{
    private readonly string _path;
    private readonly string? _corruptDir;
    private readonly object _lock = new();

    public SettingsService(string path, string? corruptDir = null)
    {
        _path = path;
        _corruptDir = corruptDir;
        Current = new JarvisSettings().Normalize();
    }

    public JarvisSettings Current { get; private set; }
    public LoadStatus LastLoadStatus { get; private set; }
    public string? LastLoadMessage { get; private set; }
    public event Action<JarvisSettings>? Changed;

    public JarvisSettings Load()
    {
        var r = JsonStore.Load(_path, JarvisSettings.CurrentSchema, () => new JarvisSettings(), _corruptDir);
        lock (_lock) Current = r.Value.Normalize();
        LastLoadStatus = r.Status;
        LastLoadMessage = r.Status switch
        {
            LoadStatus.Corrupt => $"Файл настроек повреждён и сохранён как {r.BackupPath ?? "(не удалось сохранить копию)"}. Загружены настройки по умолчанию.",
            LoadStatus.NewerSchema => r.Error,
            _ => null,
        };
        if (r.Status is LoadStatus.Missing or LoadStatus.Corrupt) Save();
        return Current;
    }

    public void Save()
    {
        lock (_lock)
        {
            Current.Normalize();
            JsonStore.Save(_path, JarvisSettings.CurrentSchema, Current);
        }
        Changed?.Invoke(Current);
    }

    public void Update(Action<JarvisSettings> change)
    {
        lock (_lock) change(Current);
        Save();
    }
}
