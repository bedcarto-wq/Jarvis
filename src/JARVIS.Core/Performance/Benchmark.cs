using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Jarvis.Core.Performance;

/// <summary>Одно измерение. Value == null и Text == null означает «Недоступно» (причина в Note).</summary>
public sealed record Measurement(string Key, string Group, string Title, double? Value, string Unit, string? Text = null, string? Note = null)
{
    [JsonIgnore] public bool Available => Value is not null || Text is not null;

    public static Measurement Unavailable(string key, string group, string title, string reason) =>
        new(key, group, title, null, "", null, reason);

    [JsonIgnore] public string Display => Value is { } v
        ? (Math.Abs(v) >= 100 ? v.ToString("N0") : v.ToString("0.##")) + (Unit.Length > 0 ? " " + Unit : "") + (Text is null ? "" : $" ({Text})")
        : Text ?? "Недоступно";
}

/// <summary>Ключи измерений, на которые опирается подбор рекомендаций.</summary>
public static class MKeys
{
    public const string CpuName = "sys.cpuName", LogicalCpus = "sys.logicalCpus", Arch = "sys.arch", RamTotalMb = "sys.ramTotalMb",
        RamAvailableMb = "sys.ramAvailableMb", CpuLoad = "sys.cpuLoadPercent", DiskFreeGb = "sys.diskFreeGb", DiskKind = "sys.diskKind",
        Os = "sys.os", Screen = "sys.screen";
    public const string SelfCpuIdle = "self.cpuIdlePercent", SelfWorkingSetMb = "self.workingSetMb", SelfPrivateMb = "self.privateMb",
        SelfThreads = "self.threads", SelfGcHeapMb = "self.gcHeapMb";
    public const string VoiceModel = "voice.model", VoiceInitMs = "voice.initMs", VoiceDecodeMs = "voice.decodeMs", VoiceRtf = "voice.rtf",
        VoiceCommands = "voice.commands", VoiceMic = "voice.microphone", VoiceAccuracy = "voice.accuracy", VoiceMemMb = "voice.memMb";
    public const string OcrInitMs = "ocr.initMs", OcrMsScale1 = "ocr.ms.scale1", OcrMsScale15 = "ocr.ms.scale15", OcrMsScale2 = "ocr.ms.scale2",
        OcrAccuracy = "ocr.accuracy", OcrMemMb = "ocr.memMb";
    public const string UiaWindowsMs = "uia.windowsMs", UiaSnapshotMs = "uia.snapshotMs", UiaFindMs = "uia.findMs", UiaRecheckMs = "uia.recheckMs",
        UiaInvoke = "uia.invoke", UiaReadMs = "uia.readMs";
    public const string TplStepOverheadMs = "tpl.stepOverheadMs", TplConditionMs = "tpl.conditionMs", TplWaitErrorMs = "tpl.waitErrorMs",
        TplCancelMs = "tpl.cancelMs", TplErrorHandled = "tpl.errorHandled", TplParallelMs = "tpl.parallelMs", TplMemMb = "tpl.memMb";
    public const string DiskWriteMs = "disk.writeMs", DiskReadMs = "disk.readMs", DiskSmallWriteMs = "disk.smallWriteMs", DiskExtra = "disk.extra";
}

public enum BenchmarkStage { SystemInfo, Measuring, Analysis, Matching, Done }

public static class BenchmarkStageNames
{
    public static string Ru(this BenchmarkStage s) => s switch
    {
        BenchmarkStage.SystemInfo => "Сбор системных сведений",
        BenchmarkStage.Measuring => "Проверка производительности",
        BenchmarkStage.Analysis => "Анализ результатов",
        BenchmarkStage.Matching => "Подбор профиля",
        _ => "Готово",
    };
}

/// <summary>Явный способ запуска. Автоматического варианта нет намеренно.</summary>
public enum BenchmarkTrigger { Button, VoiceCommand }

public sealed record BenchmarkRequest(BenchmarkTrigger Trigger, bool AllowMicrophoneCheck = false);

public sealed class BenchmarkContext
{
    public required BenchmarkRequest Request { get; init; }
    public required IReadOnlyDictionary<string, double> CurrentParameters { get; init; }
}

/// <summary>Отдельная безопасная проверка в составе одного комплексного бенчмарка.</summary>
public interface IBenchmarkProbe
{
    string Title { get; }
    BenchmarkStage Stage { get; }
    Task<IReadOnlyList<Measurement>> RunAsync(BenchmarkContext ctx, CancellationToken ct);
}

public sealed record ProbeStatus(string Title, string Status, double DurationMs, string? Error);

public sealed record BenchmarkProgress(BenchmarkStage Stage, string Message, int Completed, int Total);

public sealed class BenchmarkResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartedAt { get; set; }
    public double DurationSeconds { get; set; }
    public string AppVersion { get; set; } = "";
    public BenchmarkTrigger Trigger { get; set; }
    public bool Cancelled { get; set; }
    public string? ActiveProfileId { get; set; }
    public List<Measurement> Measurements { get; set; } = [];
    public List<ProbeStatus> Probes { get; set; } = [];
    public RecommendationReport? Recommendation { get; set; }

    public double? Get(string key) => Measurements.FirstOrDefault(m => m.Key == key)?.Value;
    [JsonIgnore] public IEnumerable<Measurement> UnavailableMeasurements => Measurements.Where(m => !m.Available);
}

/// <summary>
/// Оркестрация одного комплексного бенчмарка. Запускается ТОЛЬКО вызовом <see cref="RunAsync"/>
/// по явному запросу (кнопка/голосовая команда). Таймеров и автозапуска нет; параллельный
/// повторный запуск отклоняется. Рекомендации не применяются — только формируются.
/// </summary>
public sealed class BenchmarkService
{
    private readonly Func<IReadOnlyList<IBenchmarkProbe>> _probes;
    private readonly RecommendationService _recommendations;
    private readonly Func<IReadOnlyDictionary<string, double>> _currentParameters;
    private readonly Func<string?> _activeProfile;
    private readonly PerformanceProfileService _profiles;
    private readonly BenchmarkReportStore? _store;
    private readonly string _appVersion;
    private int _running;

    public BenchmarkService(Func<IReadOnlyList<IBenchmarkProbe>> probes, RecommendationService recommendations,
        Func<IReadOnlyDictionary<string, double>> currentParameters, Func<string?> activeProfile,
        PerformanceProfileService profiles, BenchmarkReportStore? store, string appVersion)
    {
        _probes = probes;
        _recommendations = recommendations;
        _currentParameters = currentParameters;
        _activeProfile = activeProfile;
        _profiles = profiles;
        _store = store;
        _appVersion = appVersion;
    }

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public bool IsRunning => Volatile.Read(ref _running) == 1;
    /// <summary>Сколько сеансов было запущено (для проверки отсутствия автозапуска).</summary>
    public int SessionsStarted { get; private set; }
    public event Action<BenchmarkProgress>? Progress;

    public async Task<BenchmarkResult> RunAsync(BenchmarkRequest request, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Бенчмарк уже выполняется.");
        SessionsStarted++;
        var sw = Stopwatch.StartNew();
        var result = new BenchmarkResult { StartedAt = DateTime.Now, AppVersion = _appVersion, Trigger = request.Trigger, ActiveProfileId = _activeProfile() };
        try
        {
            var current = _currentParameters();
            var ctx = new BenchmarkContext { Request = request, CurrentParameters = current };
            var probes = _probes();
            var ordered = probes.Where(p => p.Stage == BenchmarkStage.SystemInfo).Concat(probes.Where(p => p.Stage != BenchmarkStage.SystemInfo)).ToList();
            var done = 0;
            foreach (var probe in ordered)
            {
                if (ct.IsCancellationRequested) break;
                Progress?.Invoke(new BenchmarkProgress(probe.Stage, $"{probe.Title}…", done, ordered.Count));
                var psw = Stopwatch.StartNew();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ProbeTimeout);
                try
                {
                    var ms = await Task.Run(() => probe.RunAsync(ctx, timeout.Token), timeout.Token).ConfigureAwait(false);
                    result.Measurements.AddRange(ms);
                    result.Probes.Add(new ProbeStatus(probe.Title, "выполнена", psw.Elapsed.TotalMilliseconds, null));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    result.Probes.Add(new ProbeStatus(probe.Title, "отменена", psw.Elapsed.TotalMilliseconds, null));
                    break;
                }
                catch (OperationCanceledException)
                {
                    result.Probes.Add(new ProbeStatus(probe.Title, "превышено время", psw.Elapsed.TotalMilliseconds, $"Дольше {ProbeTimeout.TotalSeconds:0} с"));
                    result.Measurements.Add(Measurement.Unavailable("probe." + done, "Ошибки", probe.Title, $"Проверка не завершилась за {ProbeTimeout.TotalSeconds:0} с"));
                }
                catch (Exception ex)
                {
                    // Одна неудачная проверка не прерывает остальные.
                    result.Probes.Add(new ProbeStatus(probe.Title, "ошибка", psw.Elapsed.TotalMilliseconds, ex.Message));
                    result.Measurements.Add(Measurement.Unavailable("probe." + done, "Ошибки", probe.Title, "Ошибка: " + ex.Message));
                }
                done++;
                Progress?.Invoke(new BenchmarkProgress(probe.Stage, $"{probe.Title}: готово", done, ordered.Count));
            }

            if (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                result.DurationSeconds = sw.Elapsed.TotalSeconds;
                Progress?.Invoke(new BenchmarkProgress(BenchmarkStage.Done, "Бенчмарк отменён. Настройки не изменены.", done, ordered.Count));
                return result;
            }

            Progress?.Invoke(new BenchmarkProgress(BenchmarkStage.Analysis, "Анализ результатов…", done, ordered.Count));
            var report = _recommendations.Analyze(result.Measurements, current);
            Progress?.Invoke(new BenchmarkProgress(BenchmarkStage.Matching, "Подбор профиля…", done, ordered.Count));
            _recommendations.Match(report, _profiles.All, result.ActiveProfileId);
            result.Recommendation = report;
            result.DurationSeconds = sw.Elapsed.TotalSeconds;
            if (_store is not null)
            {
                try { await _store.SaveAsync(result).ConfigureAwait(false); }
                catch (Exception ex) { report.Notes.Add($"Отчёт не сохранён: {ex.Message}"); }
            }
            Progress?.Invoke(new BenchmarkProgress(BenchmarkStage.Done, "Готово", done, ordered.Count));
            return result;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
