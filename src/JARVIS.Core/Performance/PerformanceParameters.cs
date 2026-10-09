using System.Globalization;
using Jarvis.Core.Settings;

namespace Jarvis.Core.Performance;

/// <summary>
/// Параметры производительности, которых нет среди «старых» настроек. Каждый из них
/// реально читается компонентами (см. <see cref="PerformanceCatalog"/> — там же описано, где).
/// Значения по умолчанию совпадают с профилем «Средний ПК».
/// </summary>
public sealed class PerformanceSettings
{
    public int HudRefreshMs { get; set; } = 150;
    public int WindowPollMs { get; set; } = 250;
    public int ElementPollMs { get; set; } = 500;
    public int ElementWaitTimeoutMs { get; set; } = 10000;
    public int WindowListCacheMs { get; set; } = 300;
    public int UiaMaxElements { get; set; } = 4000;
    public int UiaWalkTimeMs { get; set; } = 2500;
    public double OcrScale { get; set; } = 2.0;
    public int OcrMaxWindows { get; set; } = 3;
    public int RecognizerMaxHmmPf { get; set; } = 3000;
    public int RecognizerBeamExp { get; set; } = 30;
    public int MaxBackgroundTasks { get; set; } = 2;
    public int MonitoringIntervalMs { get; set; } = 2000;

    public PerformanceSettings Normalize()
    {
        foreach (var d in PerformanceCatalog.All.Where(d => d.IsPerformanceSection))
        {
            // Значения вне диапазона приводятся к границам — повреждённый файл не мешает запуску.
            var s = new JarvisSettings { Performance = this };
            d.Set(s, d.Clamp(d.Get(s)));
        }
        return this;
    }
}

public enum ParamKind { Bool, Int, Double }

/// <summary>Описание одного параметра профиля: подпись, диапазон и связь с настройками.</summary>
public sealed record ParameterDescriptor(
    string Key,
    string Group,
    string Label,
    string Description,
    string Unit,
    ParamKind Kind,
    double Min,
    double Max,
    Func<JarvisSettings, double> Get,
    Action<JarvisSettings, double> Set,
    bool RequiresVoiceRestart = false,
    bool IsPerformanceSection = true)
{
    public double Clamp(double v) => Kind == ParamKind.Bool ? (v >= 0.5 ? 1 : 0) : Math.Clamp(Kind == ParamKind.Int ? Math.Round(v) : v, Min, Max);

    public bool IsValid(double v) =>
        !double.IsNaN(v) && !double.IsInfinity(v) &&
        (Kind == ParamKind.Bool ? v is 0 or 1 : v >= Min && v <= Max && (Kind != ParamKind.Int || Math.Abs(v - Math.Round(v)) < 1e-9));

    public string Format(double v) => Kind switch
    {
        ParamKind.Bool => v >= 0.5 ? "вкл." : "выкл.",
        ParamKind.Double => v.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")) + (Unit.Length > 0 ? " " + Unit : ""),
        _ => ((long)Math.Round(v)).ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) + (Unit.Length > 0 ? " " + Unit : ""),
    };

    /// <summary>Нормированная разница (0…1) для сравнения профилей.</summary>
    public double Distance(double a, double b) => Kind == ParamKind.Bool ? (Math.Abs(a - b) > 0.5 ? 1 : 0) : Math.Abs(a - b) / Math.Max(1e-9, Max - Min);
}

/// <summary>
/// Единый каталог параметров производительности. Профили — это значения для этих ключей;
/// параметры безопасности (лимиты повторов, подтверждения, стоп-слово) сюда намеренно не входят.
/// </summary>
public static class PerformanceCatalog
{
    private static double B(bool v) => v ? 1 : 0;

    public static readonly IReadOnlyList<ParameterDescriptor> All =
    [
        new("hud.animations", "Интерфейс", "Анимации HUD", "Пульсация индикатора. Выключение экономит перерисовку.", "", ParamKind.Bool, 0, 1,
            s => B(s.HudAnimations), (s, v) => s.HudAnimations = v >= 0.5, IsPerformanceSection: false),
        new("hud.refreshMs", "Интерфейс", "Обновление HUD и индикатора микрофона", "Как часто перерисовывается уровень микрофона.", "мс", ParamKind.Int, 100, 2000,
            s => s.Performance.HudRefreshMs, (s, v) => s.Performance.HudRefreshMs = (int)v),
        new("exec.stepDelayMs", "Выполнение", "Пауза между шагами", "Даёт программам время отрисовать окно перед следующим шагом.", "мс", ParamKind.Int, 0, 2000,
            s => s.StepDelayMs, (s, v) => s.StepDelayMs = (int)v, IsPerformanceSection: false),
        new("exec.typingDelayMs", "Выполнение", "Задержка между символами", "Медленные программы теряют символы при быстром вводе.", "мс", ParamKind.Int, 0, 100,
            s => s.TypingDelayMs, (s, v) => s.TypingDelayMs = (int)v, IsPerformanceSection: false),
        new("exec.launchTimeoutMs", "Выполнение", "Ожидание окна после запуска", "Сколько ждать появления окна программы. По истечении JARVIS проверяет фактическое состояние процесса.", "мс", ParamKind.Int, 1000, 30000,
            s => s.LaunchTimeoutMs, (s, v) => s.LaunchTimeoutMs = (int)v, IsPerformanceSection: false),
        new("exec.windowPollMs", "Выполнение", "Интервал проверки появления окна", "Реже — меньше нагрузка, но позже реакция.", "мс", ParamKind.Int, 100, 2000,
            s => s.Performance.WindowPollMs, (s, v) => s.Performance.WindowPollMs = (int)v),
        new("exec.elementPollMs", "Выполнение", "Интервал проверки элемента", "Для блока «Ожидание элемента»: поиск по UI Automation — дорогая операция.", "мс", ParamKind.Int, 200, 3000,
            s => s.Performance.ElementPollMs, (s, v) => s.Performance.ElementPollMs = (int)v),
        new("exec.elementWaitTimeoutMs", "Выполнение", "Ожидание элемента по умолчанию", "Если в блоке не задан свой тайм-аут.", "мс", ParamKind.Int, 1000, 120000,
            s => s.Performance.ElementWaitTimeoutMs, (s, v) => s.Performance.ElementWaitTimeoutMs = (int)v),
        new("ui.windowCacheMs", "Окна и элементы", "Кэш списка окон", "Повторное использование уже полученного списка окон. После действий с окнами кэш сбрасывается.", "мс", ParamKind.Int, 0, 5000,
            s => s.Performance.WindowListCacheMs, (s, v) => s.Performance.WindowListCacheMs = (int)v),
        new("ui.uiaMaxElements", "Окна и элементы", "Максимум элементов при обходе UI Automation", "Ограничивает нечёткий поиск элемента по имени.", "шт.", ParamKind.Int, 200, 10000,
            s => s.Performance.UiaMaxElements, (s, v) => s.Performance.UiaMaxElements = (int)v),
        new("ui.uiaWalkTimeMs", "Окна и элементы", "Лимит времени обхода UI Automation", "На медленном ПК обход идёт дольше — лимит можно увеличить.", "мс", ParamKind.Int, 500, 10000,
            s => s.Performance.UiaWalkTimeMs, (s, v) => s.Performance.UiaWalkTimeMs = (int)v),
        new("ocr.scale", "Распознавание текста (OCR)", "Увеличение снимка для OCR", "2× точнее на мелком тексте, но обрабатывает в 4 раза больше пикселей, чем 1×.", "×", ParamKind.Double, 1, 3,
            s => s.Performance.OcrScale, (s, v) => s.Performance.OcrScale = v),
        new("ocr.maxWindows", "Распознавание текста (OCR)", "Окон для OCR за один поиск", "Сколько окон-кандидатов распознавать, если UI Automation не помог.", "шт.", ParamKind.Int, 1, 5,
            s => s.Performance.OcrMaxWindows, (s, v) => s.Performance.OcrMaxWindows = (int)v),
        new("voice.maxHmmPf", "Распознавание речи", "Максимум активных HMM на кадр (maxhmmpf)", "Меньше — быстрее и легче для CPU, но может снизить точность.", "", ParamKind.Int, 500, 10000,
            s => s.Performance.RecognizerMaxHmmPf, (s, v) => s.Performance.RecognizerMaxHmmPf = (int)v, RequiresVoiceRestart: true),
        new("voice.beamExp", "Распознавание речи", "Ширина луча поиска (beam = 1e-N)", "Больше N — шире поиск: точнее, но медленнее.", "N", ParamKind.Int, 15, 60,
            s => s.Performance.RecognizerBeamExp, (s, v) => s.Performance.RecognizerBeamExp = (int)v, RequiresVoiceRestart: true),
        new("bg.maxTasks", "Фоновые задачи", "Параллельных фоновых задач", "Поиск программ, перестроение словаря и другие фоновые операции.", "шт.", ParamKind.Int, 1, 4,
            s => s.Performance.MaxBackgroundTasks, (s, v) => s.Performance.MaxBackgroundTasks = (int)v),
        new("mon.intervalMs", "Фоновые задачи", "Интервал мониторинга", "Как часто мониторинг измеряет CPU и память JARVIS (если включён).", "мс", ParamKind.Int, 1000, 30000,
            s => s.Performance.MonitoringIntervalMs, (s, v) => s.Performance.MonitoringIntervalMs = (int)v),
    ];

    private static readonly Dictionary<string, ParameterDescriptor> ByKey = All.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    public static ParameterDescriptor? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>Имена настроек безопасности, которые профиль производительности не может менять.</summary>
    public static readonly IReadOnlySet<string> ForbiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MaxRepeatCount", "MaxRetriesCap", "DefaultRetries", "MaxTemplateNesting", "StopWord", "StopReaction",
        "DangerousOperations", "DangerousHotkeys", "RequireConfirmation", "WakePhrases", "Network", "Telemetry",
        "safety.stop", "safety.confirm", "exec.maxRepeat", "exec.maxNesting",
    };

    public static Dictionary<string, double> Read(JarvisSettings s) => All.ToDictionary(d => d.Key, d => d.Get(s));
}
