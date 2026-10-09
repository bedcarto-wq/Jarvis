using System.IO;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Commands;
using Jarvis.Core.Performance;
using Jarvis.Core.Settings;
using Jarvis.Platform;
using Jarvis.Templates;
using Jarvis.Vision;
using Jarvis.Voice.Benchmark;
using Jarvis.Voice.Recognition;

namespace Jarvis.App.Services;

/// <summary>
/// Профили производительности, мониторинг и бенчмарк. Бенчмарк запускается ТОЛЬКО по явной
/// просьбе пользователя (кнопка или голосовая команда с подтверждением) — никогда при старте,
/// по таймеру, при смене профиля или импорте.
/// </summary>
public sealed partial class JarvisController
{
    public PerformanceProfileService Profiles { get; }
    public BackgroundGate Gate { get; }
    public MonitoringService Monitoring { get; private set; } = null!;
    public BenchmarkService Benchmarks { get; private set; } = null!;
    public BenchmarkReportStore Reports { get; private set; } = null!;
    public RecommendationService Recommendations { get; } = new();

    /// <summary>Голосовая команда «проведи бенчмарк»: интерфейс должен показать предупреждение.</summary>
    public event Action<BenchmarkTrigger>? BenchmarkRequested;

    private (int MaxHmmPf, int BeamExp) _voiceTuning;
    private Task _voiceRestart = Task.CompletedTask;
    private CancellationTokenSource? _benchmarkCts;
    private Action<bool>? _benchmarkPrompt;

    private void InitPerformance()
    {
        Reports = new BenchmarkReportStore(Paths.ReportsDir);
        Monitoring = new MonitoringService(() => Settings.Current.Performance.MonitoringIntervalMs,
            () => ActiveProfileTitle, () => Executor.IsRunning ? "Выполняется шаблон" : Benchmarks?.IsRunning == true ? "Бенчмарк" : "Ожидание");
        var version = typeof(JarvisController).Assembly.GetName().Version?.ToString() ?? "?";
        Benchmarks = new BenchmarkService(CreateProbes, Recommendations, () => PerformanceCatalog.Read(Settings.Current),
            () => Settings.Current.ActiveProfileId, Profiles, Reports, version);
        // Профиль не применяется заново при старте: значения уже сохранены в настройках.
        if (Profiles.Get(Settings.Current.ActiveProfileId) is null)
            Log.Info($"Активный профиль «{Settings.Current.ActiveProfileId}» не найден — используются текущие настройки.");
    }

    /// <summary>Название активного профиля с пометкой, если параметры изменены вручную.</summary>
    public string ActiveProfileTitle
    {
        get
        {
            var p = Profiles.Get(Settings.Current.ActiveProfileId);
            if (p is null) return "Свои настройки (не сохранены в профиль)";
            return IsActiveProfileModified ? $"{p.Name} (изменён)" : p.Name;
        }
    }

    public bool IsActiveProfileModified
    {
        get
        {
            var p = Profiles.Get(Settings.Current.ActiveProfileId);
            return p is not null && ProfileComparisonService.Compare(PerformanceCatalog.Read(Settings.Current), p.Parameters).Any(d => !d.Equal);
        }
    }

    private IReadOnlyList<IBenchmarkProbe> CreateProbes()
    {
        var s = Settings.Current;
        var work = Path.Combine(Path.GetTempPath(), "JARVIS-benchmark-" + Guid.NewGuid().ToString("N")[..8]);
        return
        [
            new WindowsSystemProbe(Paths.Root),
            new RuntimeSystemProbe(Paths.Root),
            new SelfLoadProbe(),
            new VoiceBenchmarkProbe(PocketSphinxModelDir, work, BuildGrammar, Paths.BenchmarkAudioDir,
                ct => Audio.CheckDeviceAsync(s.MicrophoneDeviceNumber, ct)),
            new OcrBenchmarkProbe(Paths.TessdataDir, s.OcrLanguages),
            new UiaBenchmarkProbe(Windows, Vision),
            new TemplateBenchmarkProbe(),
            new StorageProbe(Paths.Root, () => Settings.Current.ExtraStoragePath),
        ];
    }

    // ───────── применение профилей ─────────

    public sealed record ApplyOutcome(bool Success, string Message, ProfileApplyReport? Report);

    /// <summary>
    /// Применяет профиль. Об успехе сообщается только после фактического применения,
    /// включая перезапуск распознавателя, если изменились его параметры.
    /// </summary>
    public async Task<ApplyOutcome> ApplyProfileAsync(string profileId)
    {
        ProfileApplyReport report;
        try { report = Profiles.Apply(profileId, Settings); }
        catch (Exception ex) { return new ApplyOutcome(false, $"Профиль не применён: {ex.Message}", null); }
        return await FinishApplyAsync(report.Profile.Name, report.VoiceRestartRequired, report);
    }

    /// <summary>Применяет набор значений без создания профиля («применить рекомендованные настройки»).</summary>
    public async Task<ApplyOutcome> ApplyValuesAsync(IReadOnlyDictionary<string, double> values, string title)
    {
        var restart = false;
        var errors = new List<string>();
        Settings.Update(s =>
        {
            foreach (var (k, v) in values)
            {
                var d = PerformanceCatalog.Find(k);
                if (d is null) { errors.Add($"Неизвестный параметр «{k}»."); continue; }
                if (!d.IsValid(v)) { errors.Add($"«{d.Label}»: недопустимое значение."); continue; }
                if (d.Distance(d.Get(s), v) < 1e-9) continue;
                d.Set(s, v);
                restart |= d.RequiresVoiceRestart;
            }
            s.ActiveProfileId = "custom";
        });
        var r = await FinishApplyAsync(title, restart, null);
        return errors.Count == 0 ? r : r with { Message = r.Message + " Не применено: " + string.Join(" ", errors) };
    }

    private async Task<ApplyOutcome> FinishApplyAsync(string title, bool restartRequired, ProfileApplyReport? report)
    {
        var notes = report is { NotApplied.Count: > 0 } ? " Не применено: " + string.Join(" ", report.NotApplied) : "";
        if (restartRequired && Voice is not null)
        {
            await _voiceRestart;
            if (Engine is null)
                return new ApplyOutcome(false, $"Профиль «{title}» сохранён, но распознаватель не перезапустился: {VoiceStatus}.{notes}", report);
            return new ApplyOutcome(true, $"Профиль «{title}» применён, распознаватель перезапущен.{notes}", report);
        }
        return new ApplyOutcome(true, $"Профиль «{title}» применён.{notes}", report);
    }

    private void OnPerformanceSettingsChanged(JarvisSettings s)
    {
        Monitoring?.Reconfigure();
        if (s.MonitoringEnabled && Monitoring is { IsRunning: false }) Monitoring.Start();
        if (!s.MonitoringEnabled && Monitoring is { IsRunning: true }) Monitoring.Stop();
        var tuning = (s.Performance.RecognizerMaxHmmPf, s.Performance.RecognizerBeamExp);
        if (Engine is PocketSphinxEngine && tuning != _voiceTuning)
        {
            _voiceTuning = tuning;
            Log.Info("Параметры распознавателя изменились — перезапускаю голос.");
            _voiceRestart = Task.Run(StartVoice);
        }
    }

    // ───────── бенчмарк ─────────

    public bool BenchmarkRunning => Benchmarks.IsRunning;

    /// <summary>Запуск после того, как пользователь нажал «Начать» в предупреждении.</summary>
    public async Task<BenchmarkResult> RunBenchmarkAsync(BenchmarkRequest request)
    {
        if (Executor.IsRunning) throw new InvalidOperationException("Сейчас выполняется шаблон. Дождитесь окончания или скажите «стоп».");
        _benchmarkCts = new CancellationTokenSource();
        Log.Info($"Бенчмарк запущен пользователем ({(request.Trigger == BenchmarkTrigger.Button ? "кнопка" : "голосовая команда")}).");
        try
        {
            var r = await Task.Run(() => Benchmarks.RunAsync(request, _benchmarkCts.Token));
            Log.Info(r.Cancelled ? "Бенчмарк отменён." : $"Бенчмарк завершён за {r.DurationSeconds:F0} с.");
            return r;
        }
        finally
        {
            _benchmarkCts.Dispose();
            _benchmarkCts = null;
        }
    }

    public void CancelBenchmark()
    {
        try { _benchmarkCts?.Cancel(); } catch (ObjectDisposedException) { }
        _benchmarkPrompt?.Invoke(false);
    }

    private void RequestBenchmarkFromVoice()
    {
        if (Benchmarks.IsRunning) { Reply("Бенчмарк уже выполняется.", NotifyLevel.Info, speech: VoiceReplies.Working); return; }
        if (BenchmarkRequested is null) { Reply("Откройте раздел «Производительность».", NotifyLevel.Warning, speech: VoiceReplies.Cannot); return; }
        BenchmarkRequested.Invoke(BenchmarkTrigger.VoiceCommand);
        Reply("JARVIS выполнит проверку производительности. Скажите «да» или нажмите «Начать».", NotifyLevel.Info, speech: VoiceReplies.Confirm);
    }

    /// <summary>Окно предупреждения регистрирует обработчик, чтобы на него можно было ответить голосом.</summary>
    public void SetBenchmarkPrompt(Action<bool>? answer)
    {
        _benchmarkPrompt = answer;
        if (Voice is not null) Voice.ExpectingReply = answer is not null;
    }

    private bool TryAnswerBenchmarkPrompt(ParsedCommand cmd)
    {
        var p = _benchmarkPrompt;
        if (p is null || cmd.Intent is not (CommandIntent.Confirm or CommandIntent.Deny)) return false;
        p(cmd.Intent == CommandIntent.Confirm);
        return true;
    }

    // ───────── хранилище ─────────

    /// <summary>Папка для экспорта: дополнительное место (например, HDD), если доступно, иначе локальная.</summary>
    public StorageResolution ResolveExportDir() =>
        StorageLocations.Resolve(Settings.Current.ExtraStoragePath, "exports", Paths.ExportsDir);
}
