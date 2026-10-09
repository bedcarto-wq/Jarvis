using System.Text.Json;
using Jarvis.Core.Commands;
using Jarvis.Core.Storage;
using Jarvis.Core.Text;

namespace Jarvis.Templates;

public interface ITemplateRepository
{
    IReadOnlyList<Template> All { get; }
    Template? Get(Guid id);
    Template? FindByName(string name);
}

/// <summary>Хранилище шаблонов: один JSON-файл на шаблон, версия схемы, защита от повреждений.</summary>
public sealed class TemplateStore : ITemplateRepository
{
    public const int CurrentSchema = 1;
    private readonly string _dir;
    private readonly string? _corruptDir;
    private readonly object _lock = new();
    private List<Template> _templates = [];

    public TemplateStore(string directory, string? corruptDir = null)
    {
        _dir = directory;
        _corruptDir = corruptDir;
    }

    public event Action? Changed;

    public IReadOnlyList<Template> All
    {
        get { lock (_lock) return _templates.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
    }

    /// <summary>Загружает шаблоны. Возвращает сообщения о повреждённых файлах.</summary>
    public IReadOnlyList<string> Load()
    {
        var messages = new List<string>();
        var list = new List<Template>();
        Directory.CreateDirectory(_dir);
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            var r = JsonStore.Load<Template>(file, CurrentSchema, () => null!, _corruptDir);
            if (r.Status == LoadStatus.Corrupt || r.Value is null)
            {
                messages.Add($"Шаблон {Path.GetFileName(file)} повреждён ({r.Error}). Копия: {r.BackupPath ?? "не создана"}.");
                continue;
            }
            if (r.Status == LoadStatus.NewerSchema) messages.Add($"{Path.GetFileName(file)}: {r.Error}");
            Sanitize(r.Value);
            if (list.Any(t => t.Id == r.Value.Id)) r.Value.Id = Guid.NewGuid();
            list.Add(r.Value);
        }
        lock (_lock) _templates = list;
        Changed?.Invoke();
        return messages;
    }

    public Template? Get(Guid id)
    {
        lock (_lock) return _templates.FirstOrDefault(t => t.Id == id);
    }

    public Template? FindByName(string name)
    {
        var n = TextNormalizer.Normalize(name);
        lock (_lock)
            return _templates.FirstOrDefault(t => t.Id.ToString() == name)
                   ?? _templates.FirstOrDefault(t => TextNormalizer.Normalize(t.Name) == n);
    }

    public void Save(Template t)
    {
        Sanitize(t);
        t.ModifiedAt = DateTime.Now;
        JsonStore.Save(PathFor(t.Id), CurrentSchema, t);
        lock (_lock)
        {
            var i = _templates.FindIndex(x => x.Id == t.Id);
            if (i >= 0) _templates[i] = t; else _templates.Add(t);
        }
        Changed?.Invoke();
    }

    public bool Delete(Guid id)
    {
        bool removed;
        lock (_lock) removed = _templates.RemoveAll(t => t.Id == id) > 0;
        var p = PathFor(id);
        if (File.Exists(p)) File.Delete(p);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public Template Duplicate(Guid id)
    {
        var src = Get(id) ?? throw new InvalidOperationException("Шаблон не найден.");
        var copy = src.DeepCopy(UniqueName(src.Name + " (копия)"));
        copy.VoicePhrases = [];
        Save(copy);
        return copy;
    }

    public string UniqueName(string baseName)
    {
        var name = baseName;
        int i = 2;
        while (FindByName(name) is not null) name = $"{baseName} {i++}";
        return name;
    }

    public void Export(IEnumerable<Template> templates, string path)
    {
        var list = templates.ToList();
        File.WriteAllText(path, JsonStore.Serialize(CurrentSchema, list));
    }

    /// <summary>Импорт из файла (один шаблон или список). Конфликтующие Id и имена переименовываются.</summary>
    public IReadOnlyList<Template> Import(string path)
    {
        var text = File.ReadAllText(path);
        List<Template>? list = null;
        var asList = JsonStore.Parse<List<Template>>(text, null, CurrentSchema, () => null!);
        if (asList.Status is LoadStatus.Ok or LoadStatus.NewerSchema && asList.Value is not null) list = asList.Value;
        else
        {
            var single = JsonStore.Parse<Template>(text, null, CurrentSchema, () => null!);
            if (single.Status is LoadStatus.Ok or LoadStatus.NewerSchema && single.Value is not null) list = [single.Value];
            else
            {
                // Совместимость: голый шаблон без обёртки версии.
                try { var bare = JsonSerializer.Deserialize<Template>(text, JsonStore.Options); if (bare?.Steps is not null) list = [bare]; }
                catch (JsonException) { }
            }
        }
        if (list is null) throw new InvalidDataException("Файл не содержит корректных шаблонов JARVIS.");
        var imported = new List<Template>();
        foreach (var t in list.Where(t => t is not null))
        {
            if (Get(t.Id) is not null) t.Id = Guid.NewGuid();
            if (FindByName(t.Name) is not null) t.Name = UniqueName(t.Name + " (импорт)");
            Save(t);
            imported.Add(t);
        }
        return imported;
    }

    public IEnumerable<TemplatePhrase> Phrases()
    {
        foreach (var t in All.Where(t => t.Enabled))
        {
            yield return new TemplatePhrase(t.Id.ToString(), t.Name, t.Name);
            foreach (var p in t.VoicePhrases) yield return new TemplatePhrase(t.Id.ToString(), t.Name, p);
        }
    }

    private string PathFor(Guid id) => Path.Combine(_dir, id.ToString("N") + ".json");

    private static void Sanitize(Template t)
    {
        t.Name = string.IsNullOrWhiteSpace(t.Name) ? "Без названия" : t.Name.Trim();
        t.VoicePhrases = (t.VoicePhrases ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().ToList();
        t.Steps ??= [];
        foreach (var s in t.AllSteps())
        {
            s.Parameters ??= new();
            s.Then ??= [];
            s.Else ??= [];
            s.Body ??= [];
            if (s.Condition is not null) s.Condition.Parameters ??= new();
        }
    }
}
