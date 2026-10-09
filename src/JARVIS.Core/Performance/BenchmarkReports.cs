using System.Globalization;
using System.Text;
using Jarvis.Core.Storage;

namespace Jarvis.Core.Performance;

/// <summary>Локальное хранение отчётов бенчмарка (%LOCALAPPDATA%\JARVIS\reports). Ничего не отправляется.</summary>
public sealed class BenchmarkReportStore
{
    private readonly string _dir;
    public const int CurrentSchema = 1;

    public BenchmarkReportStore(string dir) => _dir = dir;

    public async Task<string> SaveAsync(BenchmarkResult result)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"benchmark-{result.StartedAt:yyyyMMdd-HHmmss}.json");
        var json = JsonStore.Serialize(CurrentSchema, result);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public IReadOnlyList<string> List() =>
        Directory.Exists(_dir) ? Directory.GetFiles(_dir, "benchmark-*.json").OrderByDescending(f => f).ToList() : [];

    /// <summary>Последний сохранённый отчёт без запуска новой проверки.</summary>
    public BenchmarkResult? LoadLatest()
    {
        foreach (var f in List())
        {
            var r = JsonStore.Load<BenchmarkResult>(f, CurrentSchema, () => null!);
            if (r.Status is LoadStatus.Ok or LoadStatus.NewerSchema && r.Value is not null) return r.Value;
        }
        return null;
    }
}

/// <summary>Экспорт отчёта бенчмарка в понятный текст (Markdown, русский язык).</summary>
public static class BenchmarkReportExporter
{
    public static string ToMarkdown(BenchmarkResult r)
    {
        var sb = new StringBuilder();
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        sb.AppendLine("# Отчёт о проверке производительности JARVIS").AppendLine();
        sb.AppendLine($"* Дата: {r.StartedAt.ToString("dd.MM.yyyy HH:mm", ru)}");
        sb.AppendLine($"* Версия JARVIS: {r.AppVersion}");
        sb.AppendLine($"* Длительность: {r.DurationSeconds:0.#} с");
        sb.AppendLine($"* Запуск: {(r.Trigger == BenchmarkTrigger.VoiceCommand ? "голосовой командой" : "кнопкой")}");
        if (r.Cancelled) sb.AppendLine("* **Проверка отменена — отчёт неполный, рекомендации не формировались.**");
        sb.AppendLine().AppendLine("> Результаты отражают состояние компьютера на момент проверки и могут устареть.").AppendLine();

        foreach (var g in r.Measurements.GroupBy(m => m.Group))
        {
            sb.AppendLine($"## {g.Key}").AppendLine().AppendLine("| Показатель | Значение | Примечание |").AppendLine("|---|---|---|");
            foreach (var m in g) sb.AppendLine($"| {m.Title} | {m.Display} | {m.Note ?? ""} |");
            sb.AppendLine();
        }
        sb.AppendLine("## Выполненные проверки").AppendLine();
        foreach (var p in r.Probes) sb.AppendLine($"* {p.Title}: {p.Status}, {p.DurationMs:0} мс{(p.Error is null ? "" : " — " + p.Error)}");
        sb.AppendLine();

        if (r.Recommendation is { } rec)
        {
            sb.AppendLine("## Итог").AppendLine().AppendLine($"Оценка: **{rec.Tier}**. {rec.Summary}").AppendLine();
            foreach (var f in rec.Findings) sb.AppendLine($"* {f}");
            if (rec.Best is { } best)
            {
                sb.AppendLine().AppendLine($"Ближайший профиль: **{best.ProfileName}** ({MatchRu(best.Match)}).");
                foreach (var d in best.Differences) sb.AppendLine($"  * {d}");
            }
            if (rec.Alternatives.Count > 0) sb.AppendLine($"Альтернативы: {string.Join(", ", rec.Alternatives.Select(a => $"{a.ProfileName} ({MatchRu(a.Match)})"))}.");
            if (rec.SuggestNewProfile) sb.AppendLine("Готовые профили подходят лишь частично — можно сохранить рекомендации как новый профиль.");
            sb.AppendLine().AppendLine("## Рекомендуемые изменения").AppendLine();
            if (rec.Changes.Count == 0) sb.AppendLine("Текущие настройки подходят — изменений не требуется.");
            else
            {
                sb.AppendLine("| Параметр | Сейчас | Рекомендуется | Почему | Ожидаемый эффект | Основание | Уверенность |").AppendLine("|---|---|---|---|---|---|---|");
                foreach (var c in rec.Changes)
                {
                    var d = PerformanceCatalog.Find(c.Key);
                    sb.AppendLine($"| {c.Label} | {d?.Format(c.Current) ?? c.Current.ToString()} | {d?.Format(c.Recommended) ?? c.Recommended.ToString()} | {c.Reason} | {c.Effect} | {c.BasedOn} | {c.Confidence.Ru()} |");
                }
            }
            if (rec.Limitations.Count > 0)
            {
                sb.AppendLine().AppendLine("## Ограничения проверки").AppendLine();
                foreach (var l in rec.Limitations.Distinct()) sb.AppendLine($"* {l}");
            }
            foreach (var n in rec.Notes) sb.AppendLine($"* {n}");
        }
        sb.AppendLine().AppendLine("Рекомендации не применяются автоматически. Отчёт хранится только на этом компьютере.");
        return sb.ToString();
    }

    public static string MatchRu(MatchKind k) => k switch
    {
        MatchKind.Exact => "полностью совпадает",
        MatchKind.Close => "почти совпадает",
        MatchKind.Partial => "подходит частично",
        _ => "не подходит",
    };
}
