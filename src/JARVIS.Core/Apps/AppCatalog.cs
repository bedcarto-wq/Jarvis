using Jarvis.Core.Storage;
using Jarvis.Core.Text;

namespace Jarvis.Core.Apps;

/// <summary>Локальная база известных программ (apps.json).</summary>
public sealed class AppCatalog
{
    public const int CurrentSchema = 1;
    private readonly string? _path;
    private readonly string? _corruptDir;
    private readonly object _lock = new();
    private List<AppEntry> _apps = [];

    public AppCatalog(string? path = null, string? corruptDir = null)
    {
        _path = path;
        _corruptDir = corruptDir;
        foreach (var d in BuiltInApps.All) _apps.Add(BuiltInApps.ToEntry(d));
    }

    public event Action? Changed;
    public bool IsInitialized { get; private set; }
    public DateTime? LastScan { get; private set; }

    public IReadOnlyList<AppEntry> All
    {
        get { lock (_lock) return _apps.Where(a => !a.Hidden).ToList(); }
    }

    public IReadOnlyList<AppEntry> AllIncludingHidden
    {
        get { lock (_lock) return _apps.ToList(); }
    }

    public string? Load()
    {
        if (_path is null) return null;
        var r = JsonStore.Load(_path, CurrentSchema, () => new CatalogData(), _corruptDir);
        if (r.Status is LoadStatus.Ok or LoadStatus.NewerSchema && r.Value.Apps.Count > 0)
        {
            lock (_lock)
            {
                _apps = r.Value.Apps;
                // Новые встроенные записи из обновлений программы.
                foreach (var d in BuiltInApps.All)
                    if (_apps.All(a => a.Id != d.Id)) _apps.Add(BuiltInApps.ToEntry(d));
            }
            IsInitialized = r.Value.Initialized;
            LastScan = r.Value.LastScan;
        }
        return r.Status == LoadStatus.Corrupt ? $"База программ повреждена, копия: {r.BackupPath}" : null;
    }

    public void Save()
    {
        if (_path is null) { Changed?.Invoke(); return; }
        lock (_lock)
            JsonStore.Save(_path, CurrentSchema, new CatalogData { Apps = _apps, Initialized = IsInitialized, LastScan = LastScan });
        Changed?.Invoke();
    }

    public AppEntry? Get(string id)
    {
        lock (_lock) return _apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void Upsert(AppEntry entry)
    {
        lock (_lock)
        {
            var i = _apps.FindIndex(a => a.Id == entry.Id);
            if (i >= 0) _apps[i] = entry; else _apps.Add(entry);
        }
        Save();
    }

    public bool Remove(string id)
    {
        bool r;
        lock (_lock) r = _apps.RemoveAll(a => a.Id == id && a.Source != AppSource.BuiltIn) > 0;
        if (r) Save();
        return r;
    }

    /// <summary>
    /// Объединяет найденные программы с базой. Пользовательские правки сохраняются,
    /// встроенные произношения добавляются к найденным записям.
    /// </summary>
    public int Merge(IEnumerable<AppEntry> discovered)
    {
        int added = 0;
        lock (_lock)
        {
            foreach (var d in discovered)
            {
                var builtIn = BuiltInApps.MatchDiscovered(d.DisplayName, d.LaunchTarget);
                if (builtIn is not null)
                {
                    var existing = _apps.First(a => a.Id == builtIn.Id);
                    if (!existing.UserEdited && (existing.LaunchTarget is null || existing.Source == AppSource.BuiltIn && builtIn.Target is null))
                    {
                        existing.LaunchKind = d.LaunchKind;
                        existing.LaunchTarget = d.LaunchTarget;
                        existing.Arguments = d.Arguments;
                        existing.WorkingDirectory = d.WorkingDirectory;
                        foreach (var p in d.ProcessNames)
                            if (!existing.ProcessNames.Contains(p, StringComparer.OrdinalIgnoreCase)) existing.ProcessNames.Add(p);
                    }
                    continue;
                }
                var id = MakeId(d.DisplayName);
                if (id.Length == 0) continue;
                var cur = _apps.FirstOrDefault(a => a.Id == id);
                if (cur is null)
                {
                    d.Id = id;
                    if (d.Aliases.Count == 0) d.Aliases = SuggestAliases(d.DisplayName);
                    _apps.Add(d);
                    added++;
                }
                else if (!cur.UserEdited)
                {
                    cur.LaunchKind = d.LaunchKind;
                    cur.LaunchTarget = d.LaunchTarget;
                    cur.Arguments = d.Arguments;
                    cur.WorkingDirectory = d.WorkingDirectory;
                    if (d.ProcessNames.Count > 0) cur.ProcessNames = d.ProcessNames;
                }
            }
            IsInitialized = true;
            LastScan = DateTime.Now;
        }
        Save();
        return added;
    }

    public static string MakeId(string displayName) =>
        TextNormalizer.Normalize(displayName).Replace(' ', '-');

    /// <summary>Русское произношение для латинского названия: «Notion» → «нотион».</summary>
    public static List<string> SuggestAliases(string displayName)
    {
        var list = new List<string>();
        var n = TextNormalizer.Normalize(displayName);
        if (n.Length > 0) list.Add(n);
        if (!Transliterator.ContainsCyrillic(n))
        {
            var cyr = Transliterator.ToCyrillic(n);
            if (cyr.Length > 0 && cyr != n) list.Add(cyr);
        }
        return list.Distinct().ToList();
    }

    private sealed class CatalogData
    {
        public List<AppEntry> Apps { get; set; } = [];
        public bool Initialized { get; set; }
        public DateTime? LastScan { get; set; }
    }
}
