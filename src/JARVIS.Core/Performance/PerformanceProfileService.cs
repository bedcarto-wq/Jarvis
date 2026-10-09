using System.Text.Json;
using Jarvis.Core.Settings;
using Jarvis.Core.Storage;

namespace Jarvis.Core.Performance;

public sealed record ProfileValidation(PerformanceProfile? Profile, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Profile is not null && Errors.Count == 0;
}

public sealed record AppliedChange(ParameterDescriptor Parameter, double OldValue, double NewValue);

public sealed record ProfileApplyReport(
    PerformanceProfile Profile,
    IReadOnlyList<AppliedChange> Applied,
    IReadOnlyList<string> NotApplied,
    bool VoiceRestartRequired)
{
    public bool FullySucceeded => NotApplied.Count == 0;
}

/// <summary>
/// Управление профилями производительности: встроенные (только чтение, всегда восстанавливаются),
/// предустановки-примеры и пользовательские профили в %LOCALAPPDATA%\JARVIS\profiles\*.json.
/// Ошибочный файл профиля не мешает запуску: он переносится в corrupt\ с сообщением.
/// </summary>
public sealed class PerformanceProfileService
{
    private readonly string _dir;
    private readonly string? _corruptDir;
    private readonly object _lock = new();
    private readonly List<PerformanceProfile> _user = [];

    public PerformanceProfileService(string profilesDir, string? corruptDir = null)
    {
        _dir = profilesDir;
        _corruptDir = corruptDir;
    }

    public event Action? Changed;

    public IReadOnlyList<PerformanceProfile> BuiltIn => [BuiltInProfiles.Medium, BuiltInProfiles.Low];
    public IReadOnlyList<PerformanceProfile> Presets => BuiltInProfiles.Presets;
    public IReadOnlyList<PerformanceProfile> User { get { lock (_lock) return _user.ToList(); } }
    public IReadOnlyList<PerformanceProfile> All => [.. BuiltIn, .. Presets, .. User];

    public PerformanceProfile? Get(string? id) => id is null ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>Загружает пользовательские профили; возвращает сообщения о проблемах.</summary>
    public IReadOnlyList<string> Load()
    {
        var messages = new List<string>();
        lock (_lock)
        {
            _user.Clear();
            if (!Directory.Exists(_dir)) return messages;
            foreach (var file in Directory.EnumerateFiles(_dir, "*.json").OrderBy(f => f))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex) { messages.Add($"Профиль {Path.GetFileName(file)} не прочитан: {ex.Message}"); continue; }
                var v = Parse(text);
                if (!v.IsValid)
                {
                    var backup = MoveToCorrupt(file);
                    messages.Add($"Профиль {Path.GetFileName(file)} повреждён ({string.Join("; ", v.Errors)}). " +
                                 (backup is null ? "Файл оставлен на месте." : $"Файл перенесён в {backup}."));
                    continue;
                }
                var p = v.Profile!;
                p.Kind = ProfileKind.User;
                if (string.IsNullOrWhiteSpace(p.Id) || All.Any(x => x.Id == p.Id) || _user.Any(x => x.Id == p.Id))
                    p.Id = NewId();
                _user.Add(p);
                messages.AddRange(v.Warnings.Select(w => $"Профиль «{p.Name}»: {w}"));
            }
        }
        return messages;
    }

    // ───────── создание и изменение ─────────

    public PerformanceProfile Create(string name, string? basedOnId = null, string? description = null)
    {
        var baseProfile = Get(basedOnId) ?? BuiltInProfiles.Medium;
        var p = new PerformanceProfile
        {
            Id = NewId(),
            Name = UniqueName(name),
            Kind = ProfileKind.User,
            Description = description ?? (basedOnId is null ? "Пользовательский профиль" : $"На основе «{baseProfile.Name}»"),
            BasedOn = baseProfile.Kind == ProfileKind.User ? baseProfile.BasedOn : baseProfile.Id,
            Parameters = new Dictionary<string, double>(baseProfile.Parameters, StringComparer.OrdinalIgnoreCase),
        };
        AddAndSave(p);
        return p;
    }

    /// <summary>Создаёт пользовательский профиль из произвольного набора значений (например, рекомендаций бенчмарка).</summary>
    public PerformanceProfile CreateFromValues(string name, IReadOnlyDictionary<string, double> values, string? description, string? basedOnId)
    {
        var p = new PerformanceProfile
        {
            Id = NewId(), Name = UniqueName(name), Kind = ProfileKind.User, Description = description, BasedOn = basedOnId,
            Parameters = PerformanceCatalog.All.ToDictionary(d => d.Key, d => d.Clamp(values.TryGetValue(d.Key, out var v) ? v : BuiltInProfiles.Medium.Parameters[d.Key]), StringComparer.OrdinalIgnoreCase),
        };
        AddAndSave(p);
        return p;
    }

    public PerformanceProfile Copy(string id, string? newName = null)
    {
        var src = Get(id) ?? throw new InvalidOperationException("Профиль не найден.");
        var p = src.Clone(NewId(), UniqueName(newName ?? src.Name + " (копия)"), ProfileKind.User);
        p.BasedOn = src.Kind == ProfileKind.User ? src.BasedOn : src.Id;
        AddAndSave(p);
        return p;
    }

    public void Rename(string id, string newName)
    {
        var p = RequireUser(id);
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Название не может быть пустым.");
        if (All.Any(x => x.Id != id && string.Equals(x.Name, newName.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException($"Профиль «{newName.Trim()}» уже существует.");
        p.Name = newName.Trim();
        Save(p);
    }

    /// <summary>Изменяет значения пользовательского профиля. Недопустимые значения отклоняются целиком.</summary>
    public IReadOnlyList<string> UpdateValues(string id, IReadOnlyDictionary<string, double> values, string? description = null)
    {
        var p = RequireUser(id);
        var errors = new List<string>();
        foreach (var (k, v) in values)
        {
            var d = PerformanceCatalog.Find(k);
            if (d is null) { errors.Add($"Неизвестный параметр «{k}»."); continue; }
            if (!d.IsValid(v)) errors.Add($"«{d.Label}»: значение {v} вне диапазона {d.Format(d.Min)} … {d.Format(d.Max)}.");
        }
        if (errors.Count > 0) return errors;
        foreach (var (k, v) in values) p.Parameters[PerformanceCatalog.Find(k)!.Key] = v;
        if (description is not null) p.Description = description;
        Save(p);
        return [];
    }

    /// <summary>Возвращает пользовательский профиль к значениям профиля-основы (или «Средний ПК»).</summary>
    public void ResetToOriginal(string id)
    {
        var p = RequireUser(id);
        var baseProfile = Get(p.BasedOn) is { Kind: not ProfileKind.User } b ? b : BuiltInProfiles.Medium;
        p.Parameters = new Dictionary<string, double>(baseProfile.Parameters, StringComparer.OrdinalIgnoreCase);
        Save(p);
    }

    public void Delete(string id)
    {
        var p = RequireUser(id);
        lock (_lock) _user.Remove(p);
        try { File.Delete(PathFor(p)); } catch (IOException) { }
        Changed?.Invoke();
    }

    // ───────── применение ─────────

    /// <summary>
    /// Проверяет профиль и применяет допустимые значения к настройкам. Неприменимые параметры
    /// перечисляются в отчёте. Параметры безопасности профилем не затрагиваются.
    /// </summary>
    public ProfileApplyReport Apply(string id, SettingsService settings)
    {
        var p = Get(id) ?? throw new InvalidOperationException("Профиль не найден.");
        var applied = new List<AppliedChange>();
        var notApplied = new List<string>();
        var restart = false;
        settings.Update(s =>
        {
            foreach (var d in PerformanceCatalog.All)
            {
                if (!p.Parameters.TryGetValue(d.Key, out var v))
                {
                    notApplied.Add($"«{d.Label}»: нет значения в профиле — оставлено {d.Format(d.Get(s))}.");
                    continue;
                }
                if (!d.IsValid(v)) { notApplied.Add($"«{d.Label}»: недопустимое значение {v}."); continue; }
                var old = d.Get(s);
                if (d.Distance(old, v) < 1e-9) continue;
                d.Set(s, v);
                applied.Add(new AppliedChange(d, old, v));
                restart |= d.RequiresVoiceRestart;
            }
            foreach (var k in p.Parameters.Keys.Where(k => PerformanceCatalog.Find(k) is null))
                notApplied.Add($"Неизвестный параметр «{k}» не применён.");
            s.ActiveProfileId = p.Id;
        });
        return new ProfileApplyReport(p, applied, notApplied, restart);
    }

    /// <summary>Какие параметры изменятся при применении профиля (без применения).</summary>
    public IReadOnlyList<ParameterDiff> PreviewChanges(string id, JarvisSettings current)
    {
        var p = Get(id) ?? throw new InvalidOperationException("Профиль не найден.");
        return ProfileComparisonService.Compare(PerformanceCatalog.Read(current), p.Parameters).Where(x => !x.Equal).ToList();
    }

    // ───────── импорт / экспорт ─────────

    public void Export(string id, string path)
    {
        var p = Get(id) ?? throw new InvalidOperationException("Профиль не найден.");
        var copy = p.Clone();
        File.WriteAllText(path, JsonStore.Serialize(PerformanceProfile.CurrentSchema, copy));
    }

    public ProfileValidation ValidateFile(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex) { return new ProfileValidation(null, [$"Файл не прочитан: {ex.Message}"], []); }
    }

    /// <summary>Импортирует проверенный профиль как пользовательский (не применяет его).</summary>
    public PerformanceProfile Import(ProfileValidation validation)
    {
        if (!validation.IsValid) throw new InvalidOperationException("Профиль содержит ошибки и не может быть импортирован.");
        var p = validation.Profile!.Clone(NewId(), UniqueName(validation.Profile!.Name), ProfileKind.User);
        AddAndSave(p);
        return p;
    }

    /// <summary>Разбор и проверка JSON профиля, включая миграцию старого формата.</summary>
    public static ProfileValidation Parse(string json)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { return new ProfileValidation(null, [$"Некорректный JSON: {ex.Message}"], []); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ProfileValidation(null, ["Файл не является профилем JARVIS."], []);
            var schema = 0;
            var data = root;
            if (TryProp(root, "SchemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number) schema = sv.GetInt32();
            if (TryProp(root, "Data", out var d) && d.ValueKind == JsonValueKind.Object) data = d;
            if (schema > PerformanceProfile.CurrentSchema)
                return new ProfileValidation(null, [$"Профиль создан более новой версией JARVIS (схема {schema}, поддерживается {PerformanceProfile.CurrentSchema})."], []);

            var p = new PerformanceProfile { Kind = ProfileKind.User };
            p.Name = Str(data, "Name")?.Trim() ?? "";
            if (p.Name.Length == 0) errors.Add("У профиля нет названия (Name).");
            p.Id = Str(data, "Id") ?? "";
            p.Description = Str(data, "Description");
            p.Purpose = Str(data, "Purpose");
            p.BasedOn = Str(data, "BasedOn");
            if (!TryProp(data, "Parameters", out var pars) || pars.ValueKind != JsonValueKind.Object)
            {
                errors.Add("В профиле нет раздела параметров (Parameters).");
                return new ProfileValidation(null, errors, warnings);
            }
            foreach (var prop in pars.EnumerateObject())
            {
                var key = schema == 0 ? MigrateKey(prop.Name) : prop.Name;
                if (PerformanceCatalog.ForbiddenKeys.Contains(prop.Name) || PerformanceCatalog.ForbiddenKeys.Contains(key))
                {
                    warnings.Add($"«{prop.Name}» — параметр безопасности; профиль производительности не может его менять. Пропущен.");
                    continue;
                }
                var desc = PerformanceCatalog.Find(key);
                if (desc is null) { warnings.Add($"Неизвестный параметр «{prop.Name}» пропущен."); continue; }
                double v;
                if (prop.Value.ValueKind == JsonValueKind.Number) v = prop.Value.GetDouble();
                else if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False && desc.Kind == ParamKind.Bool) v = prop.Value.GetBoolean() ? 1 : 0;
                else { errors.Add($"«{desc.Label}»: ожидалось число, получено «{prop.Value}»."); continue; }
                if (!desc.IsValid(v)) { errors.Add($"«{desc.Label}»: значение {v} вне допустимого диапазона {desc.Format(desc.Min)} … {desc.Format(desc.Max)}."); continue; }
                p.Parameters[desc.Key] = v;
            }
            var basis = BuiltInProfiles.All.FirstOrDefault(b => b.Id == p.BasedOn) ?? BuiltInProfiles.Medium;
            foreach (var desc in PerformanceCatalog.All.Where(x => !p.Parameters.ContainsKey(x.Key)))
            {
                p.Parameters[desc.Key] = basis.Parameters[desc.Key];
                warnings.Add($"«{desc.Label}» не задан — взято значение из «{basis.Name}» ({desc.Format(basis.Parameters[desc.Key])}).");
            }
            if (schema == 0) warnings.Add("Профиль старого формата (без версии схемы) преобразован в текущий.");
            return new ProfileValidation(errors.Count == 0 ? p : null, errors, warnings);
        }
    }

    /// <summary>Миграция схемы 0: ключи совпадали с именами свойств настроек.</summary>
    private static string MigrateKey(string key) => key switch
    {
        "HudAnimations" => "hud.animations",
        "HudRefreshMs" => "hud.refreshMs",
        "StepDelayMs" => "exec.stepDelayMs",
        "TypingDelayMs" => "exec.typingDelayMs",
        "LaunchTimeoutMs" => "exec.launchTimeoutMs",
        "WindowPollMs" => "exec.windowPollMs",
        "ElementPollMs" => "exec.elementPollMs",
        "ElementWaitTimeoutMs" => "exec.elementWaitTimeoutMs",
        "WindowListCacheMs" => "ui.windowCacheMs",
        "UiaMaxElements" => "ui.uiaMaxElements",
        "UiaWalkTimeMs" => "ui.uiaWalkTimeMs",
        "OcrScale" => "ocr.scale",
        "OcrMaxWindows" => "ocr.maxWindows",
        "RecognizerMaxHmmPf" => "voice.maxHmmPf",
        "RecognizerBeamExp" => "voice.beamExp",
        "MaxBackgroundTasks" => "bg.maxTasks",
        "MonitoringIntervalMs" => "mon.intervalMs",
        _ => key,
    };

    private static bool TryProp(JsonElement e, string name, out JsonElement value)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string? Str(JsonElement e, string name) =>
        TryProp(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ───────── служебное ─────────

    private PerformanceProfile RequireUser(string id)
    {
        var p = Get(id) ?? throw new InvalidOperationException("Профиль не найден.");
        if (p.Kind != ProfileKind.User) throw new InvalidOperationException($"«{p.Name}» — встроенный профиль, его нельзя изменить или удалить. Создайте копию.");
        return p;
    }

    private void AddAndSave(PerformanceProfile p)
    {
        lock (_lock) _user.Add(p);
        Save(p);
    }

    private void Save(PerformanceProfile p)
    {
        p.ModifiedAt = DateTime.Now;
        JsonStore.Save(PathFor(p), PerformanceProfile.CurrentSchema, p);
        Changed?.Invoke();
    }

    private string PathFor(PerformanceProfile p) => Path.Combine(_dir, p.Id + ".json");

    private static string NewId() => "user-" + Guid.NewGuid().ToString("N")[..10];

    private string UniqueName(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Мой профиль" : name.Trim();
        var n = baseName;
        for (var i = 2; All.Any(p => string.Equals(p.Name, n, StringComparison.CurrentCultureIgnoreCase)); i++) n = $"{baseName} {i}";
        return n;
    }

    private string? MoveToCorrupt(string file)
    {
        if (_corruptDir is null) return null;
        try
        {
            Directory.CreateDirectory(_corruptDir);
            var target = Path.Combine(_corruptDir, $"{Path.GetFileNameWithoutExtension(file)}.{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(file, target);
            return target;
        }
        catch { return null; }
    }
}
