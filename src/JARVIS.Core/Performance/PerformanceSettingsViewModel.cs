namespace Jarvis.Core.Performance;

/// <summary>Действия, доступные пользователю после бенчмарка (раздел 9.1). Ни одно не выполняется автоматически.</summary>
public enum BenchmarkResultAction
{
    UseRecommendedProfile, ChooseOtherProfile, ApplyRecommendedSettings, SaveAsNewProfile,
    CopyExistingProfile, KeepCurrent, ExportReport, Close,
}

/// <summary>
/// Логика страницы «Производительность», не зависящая от WPF: тексты, сводки параметров,
/// доступность действий. GUI только отображает эти данные и вызывает сервисы.
/// </summary>
public static class PerformanceSettingsViewModel
{
    public const string BenchmarkButton = "Провести бенчмарк";
    public const string BenchmarkExplanation =
        "JARVIS проверит производительность компонентов и предложит подходящие настройки. Тест запускается только по вашему запросу.";
    public const string BenchmarkWarning =
        "JARVIS выполнит проверку производительности. На время тестирования нагрузка на компьютер может увеличиться.";
    public const string StartButton = "Начать";
    public const string CancelButton = "Отмена";
    public const string StaleReportWarning =
        "Сохранённые результаты могут перестать отражать текущую ситуацию после замены оборудования, обновления системы " +
        "или существенного изменения конфигурации. Новый бенчмарк запускается только вручную.";
    public const string Unavailable = "Недоступно";

    /// <summary>Основные параметры для карточки профиля.</summary>
    public static readonly string[] MainKeys =
        ["hud.animations", "exec.stepDelayMs", "ui.windowCacheMs", "ui.uiaMaxElements", "ocr.scale", "voice.maxHmmPf", "bg.maxTasks", "mon.intervalMs"];

    public static IReadOnlyList<(string Label, string Value)> MainParameters(IReadOnlyDictionary<string, double> values) =>
        MainKeys.Select(PerformanceCatalog.Find).Where(d => d is not null)
            .Select(d => (d!.Label, values.TryGetValue(d.Key, out var v) ? d.Format(v) : Unavailable)).ToList();

    public static string KindRu(ProfileKind k) => k switch
    {
        ProfileKind.BuiltIn => "Базовый профиль",
        ProfileKind.Preset => "Готовый пресет",
        _ => "Пользовательский профиль",
    };

    public static string ActionRu(BenchmarkResultAction a) => a switch
    {
        BenchmarkResultAction.UseRecommendedProfile => "Использовать рекомендуемый профиль",
        BenchmarkResultAction.ChooseOtherProfile => "Выбрать другой профиль",
        BenchmarkResultAction.ApplyRecommendedSettings => "Применить рекомендуемые настройки",
        BenchmarkResultAction.SaveAsNewProfile => "Сохранить рекомендации как новый профиль",
        BenchmarkResultAction.CopyExistingProfile => "Создать копию существующего профиля",
        BenchmarkResultAction.KeepCurrent => "Оставить текущие настройки",
        BenchmarkResultAction.ExportReport => "Экспортировать отчёт",
        _ => "Закрыть",
    };

    /// <summary>Какие действия имеют смысл для данного результата.</summary>
    public static IReadOnlyList<BenchmarkResultAction> AvailableActions(BenchmarkResult r)
    {
        var list = new List<BenchmarkResultAction>();
        var rec = r.Recommendation;
        if (!r.Cancelled && rec is not null)
        {
            if (rec.Best is { } b && b.Match != MatchKind.None && b.ProfileId != r.ActiveProfileId)
                list.Add(BenchmarkResultAction.UseRecommendedProfile);
            list.Add(BenchmarkResultAction.ChooseOtherProfile);
            if (rec.Changes.Count > 0)
            {
                list.Add(BenchmarkResultAction.ApplyRecommendedSettings);
                list.Add(BenchmarkResultAction.SaveAsNewProfile);
            }
            list.Add(BenchmarkResultAction.CopyExistingProfile);
            list.Add(BenchmarkResultAction.KeepCurrent);
            list.Add(BenchmarkResultAction.ExportReport);
        }
        list.Add(BenchmarkResultAction.Close);
        return list;
    }

    public static string ProgressText(BenchmarkProgress p) =>
        p.Total > 0 ? $"{p.Stage.Ru()}: {p.Message} ({p.Completed} из {p.Total})" : $"{p.Stage.Ru()}: {p.Message}";

    /// <summary>Значение показателя мониторинга: число или «Недоступно», никогда не ноль-заглушка.</summary>
    public static string Metric(double? v, string unit, string format = "0.#") =>
        v is { } x && !double.IsNaN(x) ? x.ToString(format, System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " " + unit : Unavailable;
}
