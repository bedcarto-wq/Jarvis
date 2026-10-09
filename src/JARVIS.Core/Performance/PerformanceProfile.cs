namespace Jarvis.Core.Performance;

public enum ProfileKind { BuiltIn, Preset, User }

/// <summary>Профиль производительности: именованный набор значений параметров каталога.</summary>
public sealed class PerformanceProfile
{
    public const int CurrentSchema = 1;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Purpose { get; set; }
    public ProfileKind Kind { get; set; } = ProfileKind.User;
    /// <summary>Профиль, на основе которого создан (для «вернуть к исходным»).</summary>
    public string? BasedOn { get; set; }
    public Dictionary<string, double> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    public bool IsReadOnly => Kind != ProfileKind.User;

    public double Value(ParameterDescriptor d) =>
        Parameters.TryGetValue(d.Key, out var v) ? v : BuiltInProfiles.Medium.Parameters[d.Key];

    public PerformanceProfile Clone(string? newId = null, string? newName = null, ProfileKind? kind = null) => new()
    {
        Id = newId ?? Id,
        Name = newName ?? Name,
        Description = Description,
        Purpose = Purpose,
        Kind = kind ?? Kind,
        BasedOn = BasedOn,
        Parameters = new Dictionary<string, double>(Parameters, StringComparer.OrdinalIgnoreCase),
        CreatedAt = DateTime.Now,
        ModifiedAt = DateTime.Now,
    };

    public override string ToString() => Name;
}

/// <summary>
/// Встроенные профили. Значения обоснованы в docs/PERFORMANCE.md.
/// «Средний ПК» и «Слабый ПК» — обязательные базовые; остальные — примеры (предустановки).
/// </summary>
public static class BuiltInProfiles
{
    public const string MediumId = "builtin-medium";
    public const string LowId = "builtin-low";

    private static Dictionary<string, double> P(params (string Key, double Value)[] values)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values) d[k] = v;
        return d;
    }

    private static Dictionary<string, double> With(Dictionary<string, double> baseValues, params (string Key, double Value)[] changes)
    {
        var d = new Dictionary<string, double>(baseValues, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in changes) d[k] = v;
        return d;
    }

    public static readonly PerformanceProfile Medium = new()
    {
        Id = MediumId, Name = "Средний ПК", Kind = ProfileKind.BuiltIn,
        Description = "Баланс отзывчивости, надёжности и расхода ресурсов.",
        Purpose = "Компьютеры, на которых достаточно ресурсов для JARVIS и других программ (4+ ядра, 8+ ГБ ОЗУ).",
        Parameters = P(
            ("hud.animations", 1), ("hud.refreshMs", 150), ("exec.stepDelayMs", 150), ("exec.typingDelayMs", 5),
            ("exec.launchTimeoutMs", 5000), ("exec.windowPollMs", 250), ("exec.elementPollMs", 500), ("exec.elementWaitTimeoutMs", 10000),
            ("ui.windowCacheMs", 300), ("ui.uiaMaxElements", 4000), ("ui.uiaWalkTimeMs", 2500),
            ("ocr.scale", 2.0), ("ocr.maxWindows", 3), ("voice.maxHmmPf", 3000), ("voice.beamExp", 30),
            ("bg.maxTasks", 2), ("mon.intervalMs", 2000)),
    };

    public static readonly PerformanceProfile Low = new()
    {
        Id = LowId, Name = "Слабый ПК", Kind = ProfileKind.BuiltIn, BasedOn = null,
        Description = "Минимальная нагрузка в ожидании: без анимаций, последовательные фоновые задачи, реже проверки, кэш окон, ограниченный OCR.",
        Purpose = "Старые офисные и бюджетные ПК (2 ядра, 4 ГБ ОЗУ, HDD). Все базовые функции остаются доступными.",
        Parameters = P(
            ("hud.animations", 0), ("hud.refreshMs", 400), ("exec.stepDelayMs", 250), ("exec.typingDelayMs", 10),
            ("exec.launchTimeoutMs", 12000), ("exec.windowPollMs", 500), ("exec.elementPollMs", 1000), ("exec.elementWaitTimeoutMs", 20000),
            ("ui.windowCacheMs", 1000), ("ui.uiaMaxElements", 1500), ("ui.uiaWalkTimeMs", 4000),
            ("ocr.scale", 1.5), ("ocr.maxWindows", 1), ("voice.maxHmmPf", 1500), ("voice.beamExp", 25),
            ("bg.maxTasks", 1), ("mon.intervalMs", 5000)),
    };

    public static readonly IReadOnlyList<PerformanceProfile> Presets =
    [
        new()
        {
            Id = "preset-economy", Name = "Максимальная экономия", Kind = ProfileKind.Preset, BasedOn = LowId,
            Description = "Ещё меньше нагрузки, чем «Слабый ПК»: OCR без увеличения, редкие проверки. Распознавание может стать менее точным.",
            Purpose = "Очень слабые ПК или работа от батареи.",
            Parameters = With(Low.Parameters, ("hud.refreshMs", 1000), ("ocr.scale", 1.0), ("ui.uiaMaxElements", 800),
                ("voice.maxHmmPf", 1000), ("voice.beamExp", 22), ("exec.windowPollMs", 800), ("exec.elementPollMs", 1500),
                ("ui.windowCacheMs", 2000), ("mon.intervalMs", 10000)),
        },
        new()
        {
            Id = "preset-balanced", Name = "Сбалансированный", Kind = ProfileKind.Preset, BasedOn = MediumId,
            Description = "Чуть экономнее «Среднего ПК», анимации сохранены.",
            Purpose = "Ноутбуки и ПК среднего уровня, где параллельно работают тяжёлые программы.",
            Parameters = With(Medium.Parameters, ("hud.refreshMs", 250), ("ui.windowCacheMs", 500), ("ocr.scale", 1.75),
                ("voice.maxHmmPf", 2500), ("mon.intervalMs", 3000)),
        },
        new()
        {
            Id = "preset-fast", Name = "Быстрый отклик", Kind = ProfileKind.Preset, BasedOn = MediumId,
            Description = "Частые проверки и широкий поиск распознавателя. Больше нагрузка на CPU.",
            Purpose = "Мощные ПК (8+ потоков), где важна скорость реакции.",
            Parameters = With(Medium.Parameters, ("hud.refreshMs", 100), ("exec.stepDelayMs", 80), ("exec.windowPollMs", 150),
                ("exec.elementPollMs", 300), ("ui.windowCacheMs", 150), ("ui.uiaMaxElements", 5000), ("voice.maxHmmPf", 4000),
                ("voice.beamExp", 35), ("bg.maxTasks", 3)),
        },
        new()
        {
            Id = "preset-multiapp", Name = "Работа с несколькими приложениями", Kind = ProfileKind.Preset, BasedOn = MediumId,
            Description = "Больше окон для OCR, глубже обход интерфейса, дольше ожидание запуска.",
            Purpose = "Много одновременно открытых программ, сценарии с переключением окон.",
            Parameters = With(Medium.Parameters, ("exec.launchTimeoutMs", 8000), ("ocr.maxWindows", 5), ("ui.windowCacheMs", 500),
                ("ui.uiaMaxElements", 6000), ("ui.uiaWalkTimeMs", 3500), ("hud.refreshMs", 200)),
        },
    ];

    public static IReadOnlyList<PerformanceProfile> All => [Medium, Low, .. Presets];
}
