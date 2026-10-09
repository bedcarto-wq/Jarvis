using System.Text.RegularExpressions;
using Jarvis.Core.Commands;
using Jarvis.Core.Execution;
using Jarvis.Core.Performance;
using Jarvis.Core.Settings;
using Jarvis.Templates;
using Jarvis.Core.Apps;
using Jarvis.Voice.Benchmark;
using Jarvis.Voice.Recognition;

namespace Jarvis.Tests;

/// <summary>Профили производительности, бенчмарк, рекомендации, хранение (раздел 18 дополнения).</summary>
public class PerformanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-perf-" + Guid.NewGuid().ToString("N"));
    private string Profiles => Path.Combine(_root, "profiles");
    private string Corrupt => Path.Combine(_root, "corrupt");
    private string Reports => Path.Combine(_root, "reports");
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private PerformanceProfileService NewProfiles()
    {
        var s = new PerformanceProfileService(Profiles, Corrupt);
        s.Load();
        return s;
    }

    private SettingsService NewSettings()
    {
        Directory.CreateDirectory(_root);
        var s = new SettingsService(Path.Combine(_root, "settings.json"), Corrupt);
        s.Load();
        return s;
    }

    private sealed class FakeProbe(string title, BenchmarkStage stage, Func<BenchmarkContext, CancellationToken, Task<IReadOnlyList<Measurement>>> run) : IBenchmarkProbe
    {
        public int Runs;
        public string Title => title;
        public BenchmarkStage Stage => stage;
        public Task<IReadOnlyList<Measurement>> RunAsync(BenchmarkContext ctx, CancellationToken ct) { Runs++; return run(ctx, ct); }
    }

    private static FakeProbe Fixed(params Measurement[] m) =>
        new("Тестовый замер", BenchmarkStage.Measuring, (_, _) => Task.FromResult<IReadOnlyList<Measurement>>(m));

    private static Measurement M(string key, double v, string unit = "мс") => new(key, "Тест", key, v, unit);

    private static Measurement[] SlowPc() =>
    [
        M(MKeys.LogicalCpus, 2, ""), M(MKeys.RamTotalMb, 4000, "МБ"), M(MKeys.RamAvailableMb, 1200, "МБ"),
        M(MKeys.VoiceRtf, 0.8, ""), M(MKeys.OcrMsScale2, 4200), M(MKeys.UiaSnapshotMs, 1600), M(MKeys.DiskWriteMs, 900),
    ];

    private static Measurement[] FastPc() =>
    [
        M(MKeys.LogicalCpus, 16, ""), M(MKeys.RamTotalMb, 32000, "МБ"), M(MKeys.RamAvailableMb, 20000, "МБ"),
        M(MKeys.VoiceRtf, 0.05, ""), M(MKeys.OcrMsScale2, 300), M(MKeys.UiaSnapshotMs, 80), M(MKeys.DiskWriteMs, 20),
    ];

    private (BenchmarkService Svc, PerformanceProfileService Profiles, SettingsService Settings) NewBenchmark(params IBenchmarkProbe[] probes)
    {
        var profiles = NewProfiles();
        var settings = NewSettings();
        var svc = new BenchmarkService(() => probes, new RecommendationService(), () => PerformanceCatalog.Read(settings.Current),
            () => settings.Current.ActiveProfileId, profiles, new BenchmarkReportStore(Reports), "test") { ProbeTimeout = TimeSpan.FromSeconds(5) };
        return (svc, profiles, settings);
    }

    // 1–2. Базовые профили.
    [Fact]
    public void TwoBuiltInProfiles_ExistAndDifferInRealParameters()
    {
        var p = NewProfiles();
        Assert.Equal(2, p.BuiltIn.Count);
        Assert.Contains(p.BuiltIn, x => x.Name == "Средний ПК");
        Assert.Contains(p.BuiltIn, x => x.Name == "Слабый ПК");
        var diff = ProfileComparisonService.Compare(BuiltInProfiles.Medium, BuiltInProfiles.Low).Where(d => !d.Equal).ToList();
        Assert.True(diff.Count >= 8, $"различий: {diff.Count}");
        Assert.Contains(diff, d => d.Parameter.Key == "voice.maxHmmPf");
        Assert.Contains(diff, d => d.Parameter.Key == "hud.animations");
        foreach (var d in PerformanceCatalog.All)
        {
            Assert.True(d.IsValid(BuiltInProfiles.Medium.Value(d)), d.Key);
            Assert.True(d.IsValid(BuiltInProfiles.Low.Value(d)), d.Key);
        }
    }

    [Fact]
    public void FourPresets_Exist_WithRussianNames()
    {
        var names = NewProfiles().Presets.Select(x => x.Name).ToList();
        Assert.Contains("Максимальная экономия", names);
        Assert.Contains("Сбалансированный", names);
        Assert.Contains("Быстрый отклик", names);
        Assert.Contains("Работа с несколькими приложениями", names);
    }

    [Fact]
    public void BuiltInProfiles_CannotBeDeletedOrRenamed()
    {
        var p = NewProfiles();
        Assert.ThrowsAny<Exception>(() => p.Delete(BuiltInProfiles.MediumId));
        Assert.ThrowsAny<Exception>(() => p.Rename(BuiltInProfiles.LowId, "x"));
        Assert.NotNull(p.Get(BuiltInProfiles.MediumId));
    }

    // 3–5. Пользовательские профили.
    [Fact]
    public void UserProfile_CreateCopyRenameEditSave_SurvivesReload()
    {
        var p = NewProfiles();
        var a = p.Create("Мой ноутбук", BuiltInProfiles.LowId, "для поездок");
        var vals = new Dictionary<string, double>(a.Parameters) { ["exec.stepDelayMs"] = 333 };
        Assert.Empty(p.UpdateValues(a.Id, vals));
        var b = p.Copy(a.Id, "Копия ноутбука");
        p.Rename(b.Id, "Домашний");

        var reloaded = NewProfiles();
        var a2 = reloaded.User.Single(x => x.Name == "Мой ноутбук");
        Assert.Equal(333, a2.Parameters["exec.stepDelayMs"]);
        Assert.Equal("для поездок", a2.Description);
        Assert.Contains(reloaded.User, x => x.Name == "Домашний" && x.Parameters["exec.stepDelayMs"] == 333);

        reloaded.ResetToOriginal(a2.Id);
        Assert.Equal(BuiltInProfiles.Low.Parameters["exec.stepDelayMs"], reloaded.Get(a2.Id)!.Parameters["exec.stepDelayMs"]);
        reloaded.Delete(a2.Id);
        Assert.Null(NewProfiles().Get(a2.Id));
    }

    [Fact]
    public void UserProfile_RejectsInvalidValues_AndDuplicateNames()
    {
        var p = NewProfiles();
        var a = p.Create("Профиль А");
        var bad = new Dictionary<string, double>(a.Parameters) { ["ocr.scale"] = 99 };
        Assert.NotEmpty(p.UpdateValues(a.Id, bad));
        Assert.NotEqual(99, p.Get(a.Id)!.Parameters["ocr.scale"]);
        var dup = p.Create("Профиль А");
        Assert.NotEqual("Профиль А", dup.Name);
        Assert.ThrowsAny<Exception>(() => p.Rename(dup.Id, "Профиль А"));
    }

    [Fact]
    public void ExportImport_RoundTrip_CreatesNewUserProfile()
    {
        var p = NewProfiles();
        var a = p.Create("Экспортный", BuiltInProfiles.MediumId);
        var file = Path.Combine(_root, "out.json");
        p.Export(a.Id, file);
        var v = p.ValidateFile(file);
        Assert.True(v.IsValid, string.Join(";", v.Errors));
        var imported = p.Import(v);
        Assert.NotEqual(a.Id, imported.Id);
        Assert.Equal(ProfileKind.User, imported.Kind);
        Assert.DoesNotContain(ProfileComparisonService.Compare(a, imported), d => !d.Equal);
    }

    [Theory]
    [InlineData("{ это не json")]
    [InlineData("{\"SchemaVersion\": 99, \"Data\": {\"Name\": \"x\", \"Parameters\": {}}}")]
    [InlineData("{\"SchemaVersion\": 1, \"Data\": {\"Name\": \"\", \"Parameters\": {}}}")]
    [InlineData("{\"SchemaVersion\": 1, \"Data\": {\"Name\": \"Плохой\", \"Parameters\": {\"ocr.scale\": 50}}}")]
    public void Import_InvalidFile_IsRejectedWithRussianError(string json)
    {
        var v = PerformanceProfileService.Parse(json);
        Assert.False(v.IsValid);
        Assert.NotEmpty(v.Errors);
        Assert.Matches("[а-яА-ЯёЁ]", v.Errors[0]);
    }

    [Fact]
    public void Import_IgnoresForbiddenSafetyKeys_WithWarning()
    {
        var json = "{\"SchemaVersion\":1,\"Data\":{\"Name\":\"Хитрый\",\"Parameters\":{\"exec.stepDelayMs\":100,\"" + PerformanceCatalog.ForbiddenKeys.First() + "\":0}}}";
        var v = PerformanceProfileService.Parse(json);
        Assert.True(v.IsValid, string.Join(";", v.Errors));
        Assert.NotEmpty(v.Warnings);
        Assert.DoesNotContain(v.Profile!.Parameters.Keys, k => PerformanceCatalog.ForbiddenKeys.Contains(k));
    }

    [Fact]
    public void CorruptProfileFile_DoesNotBreakLoading_AndIsMovedAside()
    {
        Directory.CreateDirectory(Profiles);
        File.WriteAllText(Path.Combine(Profiles, "broken.json"), "{{{ мусор");
        var p = new PerformanceProfileService(Profiles, Corrupt);
        var messages = p.Load();
        Assert.NotEmpty(messages);
        Assert.Equal(2, p.BuiltIn.Count);
        Assert.Empty(p.User);
        Assert.False(File.Exists(Path.Combine(Profiles, "broken.json")));
    }

    // 6–7. Применение и сравнение.
    [Fact]
    public void ApplyProfile_ChangesSettings_ButNotSafety_AndPersists()
    {
        var p = NewProfiles();
        var settings = NewSettings();
        var safetyBefore = System.Text.Json.JsonSerializer.Serialize(new { settings.Current.WakePhrases, settings.Current.StopWord });
        var r = p.Apply(BuiltInProfiles.LowId, settings);
        Assert.True(r.FullySucceeded);
        Assert.NotEmpty(r.Applied);
        Assert.True(r.VoiceRestartRequired);
        Assert.Equal(BuiltInProfiles.LowId, settings.Current.ActiveProfileId);
        Assert.False(settings.Current.HudAnimations);

        var reloaded = NewSettings();
        Assert.Equal(BuiltInProfiles.LowId, reloaded.Current.ActiveProfileId);
        Assert.DoesNotContain(ProfileComparisonService.Compare(PerformanceCatalog.Read(reloaded.Current), BuiltInProfiles.Low.Parameters), d => !d.Equal);
        Assert.Equal(safetyBefore, System.Text.Json.JsonSerializer.Serialize(new { reloaded.Current.WakePhrases, reloaded.Current.StopWord }));
    }

    [Fact]
    public void Catalog_DoesNotContainSafetyParameters()
    {
        foreach (var d in PerformanceCatalog.All)
            Assert.DoesNotContain(d.Key, PerformanceCatalog.ForbiddenKeys);
        Assert.DoesNotContain(PerformanceCatalog.All, d => Regex.IsMatch(d.Key, "confirm|danger|security|stop", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void CompareProfiles_ReportsExactDifferences()
    {
        var p = NewProfiles();
        var a = p.Create("А", BuiltInProfiles.MediumId);
        var b = p.Copy(a.Id, "Б");
        Assert.All(ProfileComparisonService.Compare(a, b), d => Assert.True(d.Equal));
        p.UpdateValues(b.Id, new Dictionary<string, double>(b.Parameters) { ["bg.maxTasks"] = 1 });
        var diffs = ProfileComparisonService.Compare(p.Get(a.Id)!, p.Get(b.Id)!).Where(d => !d.Equal).ToList();
        Assert.Single(diffs);
        Assert.Equal("bg.maxTasks", diffs[0].Parameter.Key);
    }

    // 8–9. Только по запросу.
    [Fact]
    public async Task Benchmark_NeverStartsAutomatically()
    {
        var probe = Fixed(M(MKeys.VoiceRtf, 0.1, ""));
        var (svc, profiles, settings) = NewBenchmark(probe);
        Assert.Equal(0, svc.SessionsStarted);
        profiles.Apply(BuiltInProfiles.LowId, settings);
        var a = profiles.Create("Новый");
        var file = Path.Combine(_root, "p.json");
        profiles.Export(a.Id, file);
        profiles.Import(profiles.ValidateFile(file));
        settings.Update(s => s.MonitoringEnabled = true);
        var mon = new MonitoringService(() => 500, () => "x", () => "y");
        mon.Start();
        await Task.Delay(1200);
        mon.Stop();
        Assert.Equal(0, svc.SessionsStarted);
        Assert.Equal(0, probe.Runs);
        Assert.Equal(new[] { BenchmarkTrigger.Button, BenchmarkTrigger.VoiceCommand }, Enum.GetValues<BenchmarkTrigger>());
    }

    [Fact]
    public async Task Benchmark_RunsEachProbeOnce_InOneSession_AndRejectsSecondConcurrentRun()
    {
        var gate = new TaskCompletionSource();
        var slow = new FakeProbe("Медленный", BenchmarkStage.Measuring, async (_, ct) => { await gate.Task.WaitAsync(ct); return [M(MKeys.UiaSnapshotMs, 100)]; });
        var sys = new FakeProbe("Система", BenchmarkStage.SystemInfo, (_, _) => Task.FromResult<IReadOnlyList<Measurement>>([M(MKeys.LogicalCpus, 8, "")]));
        var (svc, _, _) = NewBenchmark(slow, sys);
        var stages = new List<BenchmarkStage>();
        svc.Progress += p => { lock (stages) stages.Add(p.Stage); };
        var run = svc.RunAsync(new BenchmarkRequest(BenchmarkTrigger.Button), CancellationToken.None);
        await Task.Delay(100);
        Assert.True(svc.IsRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(new BenchmarkRequest(BenchmarkTrigger.VoiceCommand), CancellationToken.None));
        gate.SetResult();
        var r = await run;
        Assert.Equal(1, slow.Runs);
        Assert.Equal(1, sys.Runs);
        Assert.Equal(1, svc.SessionsStarted);
        Assert.False(r.Cancelled);
        Assert.NotNull(r.Recommendation);
        Assert.Equal(BenchmarkStage.SystemInfo, stages[0]);
        Assert.Contains(BenchmarkStage.Analysis, stages);
        Assert.Contains(BenchmarkStage.Matching, stages);
        Assert.Single(new BenchmarkReportStore(Reports).List());
    }

    [Fact]
    public async Task Benchmark_FailedProbe_IsReportedAsUnavailable_NotZero()
    {
        var bad = new FakeProbe("Сломанный", BenchmarkStage.Measuring, (_, _) => throw new InvalidOperationException("нет устройства"));
        var good = Fixed(M(MKeys.VoiceRtf, 0.2, ""), Measurement.Unavailable(MKeys.OcrMsScale2, "OCR", "OCR", "Нет tessdata"));
        var (svc, _, _) = NewBenchmark(bad, good);
        var r = await svc.RunAsync(new BenchmarkRequest(BenchmarkTrigger.Button), CancellationToken.None);
        Assert.Contains(r.Probes, p => p.Error is not null && p.Error.Contains("нет устройства"));
        var ocr = r.Measurements.Single(m => m.Key == MKeys.OcrMsScale2);
        Assert.Null(ocr.Value);
        Assert.Equal("Недоступно", ocr.Display);
        Assert.NotNull(r.Recommendation);
        Assert.NotEmpty(r.Recommendation!.Limitations);
    }

    [Fact]
    public async Task Benchmark_Cancel_StopsWithoutRecommendationsOrSavedReport()
    {
        var hang = new FakeProbe("Долгий", BenchmarkStage.Measuring, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return []; });
        var (svc, _, settings) = NewBenchmark(hang);
        var before = System.Text.Json.JsonSerializer.Serialize(settings.Current.Performance);
        using var cts = new CancellationTokenSource(300);
        var r = await svc.RunAsync(new BenchmarkRequest(BenchmarkTrigger.Button), cts.Token);
        Assert.True(r.Cancelled);
        Assert.Null(r.Recommendation);
        Assert.False(svc.IsRunning);
        Assert.Empty(new BenchmarkReportStore(Reports).List());
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(settings.Current.Performance));
    }

    // 10–13. Рекомендации и сопоставление.
    [Fact]
    public void Recommendations_ForSlowPc_LowerCostlyParameters_WithReasons()
    {
        var rec = new RecommendationService().Analyze(SlowPc(), BuiltInProfiles.Medium.Parameters);
        Assert.NotEmpty(rec.Changes);
        foreach (var c in rec.Changes)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Reason));
            Assert.False(string.IsNullOrWhiteSpace(c.Effect));
            Assert.False(string.IsNullOrWhiteSpace(c.BasedOn));
            Assert.NotEqual(c.Current, c.Recommended);
            Assert.True(PerformanceCatalog.Find(c.Key)!.IsValid(c.Recommended));
        }
        Assert.True(rec.RecommendedParameters["voice.maxHmmPf"] < BuiltInProfiles.Medium.Parameters["voice.maxHmmPf"]);
        Assert.True(rec.RecommendedParameters["ocr.scale"] <= BuiltInProfiles.Medium.Parameters["ocr.scale"]);
    }

    [Fact]
    public void Recommendations_ForFastPc_DoNotInventChanges()
    {
        var rec = new RecommendationService().Analyze(FastPc(), BuiltInProfiles.Medium.Parameters);
        Assert.DoesNotContain(rec.Changes, c => c.Key == "voice.maxHmmPf" && c.Recommended < BuiltInProfiles.Medium.Parameters["voice.maxHmmPf"]);
        Assert.DoesNotContain(rec.Changes, c => c.Key == "hud.animations" && c.Recommended == 0);
    }

    [Fact]
    public void Matching_SlowPc_SuggestsLowProfile()
    {
        var svc = new RecommendationService();
        var rec = svc.Analyze(SlowPc(), BuiltInProfiles.Medium.Parameters);
        svc.Match(rec, NewProfiles().All, BuiltInProfiles.MediumId);
        Assert.NotNull(rec.Best);
        Assert.Contains(rec.Best!.ProfileId, new[] { BuiltInProfiles.LowId, "preset-economy" });
    }

    [Fact]
    public void Matching_PrefersUserProfile_WhenItMatchesBest()
    {
        var svc = new RecommendationService();
        var rec = svc.Analyze(SlowPc(), BuiltInProfiles.Medium.Parameters);
        var p = NewProfiles();
        var mine = p.CreateFromValues("Мой слабый ноутбук", rec.RecommendedParameters, null, null);
        svc.Match(rec, p.All, BuiltInProfiles.MediumId);
        Assert.Equal(mine.Id, rec.Best!.ProfileId);
        Assert.Equal(MatchKind.Exact, rec.Best.Match);
        Assert.Equal(ProfileKind.User, rec.Best.Kind);
    }

    [Fact]
    public async Task Benchmark_DoesNotApplyRecommendations_OrCreateProfiles()
    {
        var (svc, profiles, settings) = NewBenchmark(Fixed(SlowPc()));
        var before = System.Text.Json.JsonSerializer.Serialize(settings.Current);
        var r = await svc.RunAsync(new BenchmarkRequest(BenchmarkTrigger.VoiceCommand), CancellationToken.None);
        Assert.NotEmpty(r.Recommendation!.Changes);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(NewSettings().Current));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(settings.Current));
        Assert.Empty(profiles.User);
        Assert.Empty(NewProfiles().User);
    }

    [Fact]
    public async Task Report_SavedAndReopened_WithoutNewRun_AndExportsRussianMarkdown()
    {
        var (svc, _, _) = NewBenchmark(Fixed(SlowPc()));
        await svc.RunAsync(new BenchmarkRequest(BenchmarkTrigger.Button), CancellationToken.None);
        var store = new BenchmarkReportStore(Reports);
        var last = store.LoadLatest();
        Assert.NotNull(last);
        Assert.Equal(1, svc.SessionsStarted);
        Assert.Equal("test", last!.AppVersion);
        Assert.NotNull(last.Recommendation);
        var md = BenchmarkReportExporter.ToMarkdown(last);
        Assert.Contains("производительности", md);
        Assert.Contains("Рекоменд", md);
    }

    [Fact]
    public void ViewModel_OffersAllEightActions_AndRussianTexts()
    {
        var rec = new RecommendationService().Analyze(SlowPc(), BuiltInProfiles.Medium.Parameters);
        new RecommendationService().Match(rec, NewProfiles().All, BuiltInProfiles.MediumId);
        var r = new BenchmarkResult { ActiveProfileId = BuiltInProfiles.MediumId, Recommendation = rec };
        var actions = PerformanceSettingsViewModel.AvailableActions(r);
        Assert.Equal(8, actions.Count);
        Assert.Equal("Использовать рекомендуемый профиль", PerformanceSettingsViewModel.ActionRu(BenchmarkResultAction.UseRecommendedProfile));
        Assert.Equal("JARVIS выполнит проверку производительности. На время тестирования нагрузка на компьютер может увеличиться.",
            PerformanceSettingsViewModel.BenchmarkWarning);
        Assert.Equal("Недоступно", PerformanceSettingsViewModel.Metric(null, "%"));
        Assert.Single(PerformanceSettingsViewModel.AvailableActions(new BenchmarkResult { Cancelled = true }));
    }

    // 14. Русский язык.
    [Fact]
    public void AllParameterLabels_ProfileNames_AndStages_AreRussian()
    {
        var cyr = new Regex("[а-яА-ЯёЁ]");
        foreach (var d in PerformanceCatalog.All)
        {
            Assert.True(cyr.IsMatch(d.Label), d.Key + " label");
            Assert.True(cyr.IsMatch(d.Description), d.Key + " description");
            Assert.True(cyr.IsMatch(d.Group), d.Key + " group");
        }
        foreach (var p in BuiltInProfiles.All) { Assert.True(cyr.IsMatch(p.Name), p.Id); Assert.True(cyr.IsMatch(p.Description ?? ""), p.Id + " description"); }
        foreach (var s in Enum.GetValues<BenchmarkStage>()) Assert.Matches(cyr, s.Ru());
        foreach (var a in Enum.GetValues<BenchmarkResultAction>()) Assert.Matches(cyr, PerformanceSettingsViewModel.ActionRu(a));
        foreach (var r in VoiceReplies.All) Assert.Matches(cyr, r);
    }

    [Fact]
    public void Settings_UnknownActiveProfile_AndCorruptPerformanceSection_AreNormalized()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{\"ActiveProfileId\":\"\",\"Performance\":{\"OcrScale\":500,\"MaxBackgroundTasks\":-3}}");
        var s = new SettingsService(path, Corrupt);
        s.Load();
        Assert.Equal(BuiltInProfiles.MediumId, s.Current.ActiveProfileId);
        foreach (var d in PerformanceCatalog.All) Assert.True(d.IsValid(d.Get(s.Current)), d.Key);
    }

    // 15. Без сети.
    [Fact]
    public void Sources_ContainNoNetworkClients()
    {
        var src = FindRepoDir("src");
        var bad = new Regex(@"\b(HttpClient|WebClient|WebRequest|TcpClient|UdpClient|System\.Net\.Sockets|ClientWebSocket)\b");
        var hits = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => bad.IsMatch(File.ReadAllText(f))).ToList();
        Assert.Empty(hits);
    }

    private static string FindRepoDir(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, name))) d = d.Parent;
        return Path.Combine(d?.FullName ?? throw new DirectoryNotFoundException(name), name);
    }

    // 16. HDD / дополнительное хранилище.
    [Fact]
    public void ExtraStorage_Unavailable_FallsBackToLocal_WithMessage()
    {
        var local = Path.Combine(_root, "exports");
        var missing = OperatingSystem.IsWindows() ? @"Q:\нет-такого-диска\JARVIS" : "/proc/jarvis-нельзя-писать";
        var r = StorageLocations.Resolve(missing, "exports", local);
        Assert.True(r.UsedFallback);
        Assert.Equal(local, r.Path);
        Assert.Matches("[а-яА-Я]", r.Message!);
        Assert.True(Directory.Exists(local));

        var ok = StorageLocations.Resolve(Path.Combine(_root, "hdd"), "exports", local);
        Assert.False(ok.UsedFallback);
        Assert.True(Directory.Exists(ok.Path));
    }

    [Fact]
    public async Task StorageProbe_MeasuresDisk_AndMarksMissingExtraAsUnavailable()
    {
        Directory.CreateDirectory(_root);
        var probe = new StorageProbe(_root, () => "/proc/jarvis-нельзя-писать");
        var m = await probe.RunAsync(new BenchmarkContext { Request = new(BenchmarkTrigger.Button), CurrentParameters = new Dictionary<string, double>() }, CancellationToken.None);
        Assert.True(m.Single(x => x.Key == MKeys.DiskWriteMs).Value > 0);
        Assert.False(m.Single(x => x.Key == MKeys.DiskExtra).Value is > 0);
    }

    // 17. Остановка шаблонов внутри бенчмарка и аварийная остановка.
    [Fact]
    public async Task TemplateBenchmarkProbe_RunsInSandbox_AndMeasuresStop()
    {
        var probe = new TemplateBenchmarkProbe();
        var m = await probe.RunAsync(new BenchmarkContext { Request = new(BenchmarkTrigger.Button), CurrentParameters = PerformanceCatalog.Read(new JarvisSettings()) }, CancellationToken.None);
        Assert.True(m.Single(x => x.Key == MKeys.TplStepOverheadMs).Value >= 0);
        Assert.Equal(1, m.Single(x => x.Key == MKeys.TplErrorHandled).Value);
        var cancel = m.Single(x => x.Key == MKeys.TplCancelMs).Value;
        Assert.NotNull(cancel);
        Assert.True(cancel < 2000, $"остановка заняла {cancel} мс");
    }

    [Fact]
    public void EmergencyStop_CancelsRunToken_EvenDuringBenchmark()
    {
        var stop = new StopController();
        var t = stop.BeginRun();
        stop.StopAll();
        Assert.True(t.IsCancellationRequested);
        Assert.False(stop.BeginRun().IsCancellationRequested);
    }

    [Fact]
    public async Task BackgroundGate_LimitsParallelTasks()
    {
        var gate = new BackgroundGate(() => 2);
        var max = 0;
        var cur = 0;
        var tasks = Enumerable.Range(0, 6).Select(_ => gate.RunAsync(async () =>
        {
            var n = Interlocked.Increment(ref cur);
            lock (gate) max = Math.Max(max, n);
            await Task.Delay(50);
            Interlocked.Decrement(ref cur);
        }));
        await Task.WhenAll(tasks);
        Assert.True(max <= 2, $"параллельно: {max}");
    }

    [Fact]
    public void Monitoring_ReportsRealValues_OrNull_NeverFakeZero()
    {
        var mon = new MonitoringService(() => 1000, () => "Средний ПК", () => "Ожидание");
        var a = mon.SampleNow();
        var b = mon.SampleNow();
        Assert.Equal("Средний ПК", b.ActiveProfile);
        Assert.True(b.WorkingSetMb is null or > 0);
        Assert.True(b.Threads is null or > 0);
    }

    /// <summary>Реальный замер голосового движка (нужны JARVIS_POCKETSPHINX_LIB и JARVIS_PS_MODEL, иначе пропуск с успехом).</summary>
    [Fact]
    public async Task VoiceBenchmarkProbe_RealDecoder_MeasuresRtf_AndAccuracyUnavailableWithoutRecordings()
    {
        var model = Environment.GetEnvironmentVariable("JARVIS_PS_MODEL");
        var lib = Environment.GetEnvironmentVariable("JARVIS_POCKETSPHINX_LIB");
        var probe = new VoiceBenchmarkProbe(model ?? Path.Combine(_root, "нет-модели"), Path.Combine(_root, "voice"),
            () => VocabularyBuilder.Build(new JarvisSettings(), BuiltInApps.All.Select(BuiltInApps.ToEntry), ["рабочий режим"]),
            Path.Combine(_root, "benchmark-audio"), null);
        var m = await probe.RunAsync(new BenchmarkContext { Request = new(BenchmarkTrigger.Button), CurrentParameters = PerformanceCatalog.Read(new JarvisSettings()) }, CancellationToken.None);
        Assert.Null(m.Single(x => x.Key == MKeys.VoiceAccuracy).Value);
        Assert.Null(m.Single(x => x.Key == MKeys.VoiceMic).Value); // без разрешения микрофон не открывается
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(lib) || !Directory.Exists(model))
        {
            Assert.Null(m.Single(x => x.Key == MKeys.VoiceRtf).Value);
            return;
        }
        var rtf = m.Single(x => x.Key == MKeys.VoiceRtf).Value;
        Assert.NotNull(rtf);
        Assert.InRange(rtf!.Value, 0.0001, 5);
    }
}
