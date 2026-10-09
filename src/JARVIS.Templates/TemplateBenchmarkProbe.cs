using System.Diagnostics;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Execution;
using Jarvis.Core.Performance;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;

namespace Jarvis.Templates;

/// <summary>
/// Изолированная проверка исполнителя шаблонов. Действия выполняет «песочница»: никаких окон,
/// ввода, файлов и системных команд — измеряются накладные расходы интерпретатора, условия,
/// точность ожиданий, отмена, обработка ошибок, параллельность и память.
/// </summary>
public sealed class TemplateBenchmarkProbe : IBenchmarkProbe
{
    public string Title => "Выполнение шаблонов (безопасная песочница)";
    public BenchmarkStage Stage => BenchmarkStage.Measuring;

    public async Task<IReadOnlyList<Measurement>> RunAsync(BenchmarkContext ctx, CancellationToken ct)
    {
        const string g = "Шаблоны";
        var list = new List<Measurement>();

        // 1. Накладные расходы на шаг.
        var seq = Enumerable.Range(0, 300).Select(_ => Say()).ToList();
        var (r1, ms1) = await Run(seq, ct);
        list.Add(new(MKeys.TplStepOverheadMs, g, "Накладные расходы на шаг", ms1 / Math.Max(1, r1.StepsExecuted), "мс", null, "300 шагов без реальных действий"));

        // 2. Условные переходы.
        var cond = StepCatalog.Create(StepKind.If);
        cond.Condition = new StepCondition { Kind = ConditionKind.WindowExists };
        cond.Then.Add(Say());
        cond.Else.Add(Say());
        var rep = Repeat(200, cond);
        var (_, ms2) = await Run([rep], ct);
        list.Add(new(MKeys.TplConditionMs, g, "Условный переход (если/иначе)", ms2 / 200, "мс"));

        // 3. Точность ожиданий.
        var waits = Enumerable.Range(0, 5).Select(_ => StepCatalog.Create(StepKind.Wait).Set(P.Ms, "50")).ToList();
        var (_, ms3) = await Run(waits, ct);
        list.Add(new(MKeys.TplWaitErrorMs, g, "Отклонение ожидания 50 мс", Math.Max(0, ms3 / 5 - 50), "мс", null, "Насколько ожидание дольше заданного"));

        // 4. Обработка ошибок: шаг падает, «продолжать при ошибке» — сценарий идёт дальше.
        var bad = Say("fail");
        bad.ContinueOnError = true;
        bad.Retries = 1;
        var (r4, _) = await Run([Say(), bad, Say()], ct);
        list.Add(new(MKeys.TplErrorHandled, g, "Обработка ошибки шага", r4.Status == ExecutionStatus.Completed && r4.StepsExecuted >= 3 ? 1 : 0, "",
            r4.Status == ExecutionStatus.Completed ? "сценарий продолжен" : "ошибка: " + r4.Message));

        // 5. Отмена: длинный цикл останавливается командой «стоп».
        var stop = new StopController();
        var longRun = Repeat(1000, StepCatalog.Create(StepKind.Wait).Set(P.Ms, "20"));
        var exec = Executor(stop);
        var task = exec.RunStepsAsync("benchmark-cancel", [longRun]);
        await Task.Delay(150, ct);
        var sw = Stopwatch.StartNew();
        stop.StopAll();
        var r5 = await task;
        list.Add(new(MKeys.TplCancelMs, g, "Время реакции на остановку", sw.Elapsed.TotalMilliseconds, "мс",
            r5.Status == ExecutionStatus.Stopped ? "остановлено" : $"статус {r5.Status}"));

        // 6. Параллельные независимые сценарии (по числу фоновых задач профиля, минимум 2).
        var parallel = (int)Math.Clamp(ctx.CurrentParameters.TryGetValue("bg.maxTasks", out var bt) ? bt : 2, 2, 4);
        sw.Restart();
        await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => Run(Enumerable.Range(0, 200).Select(_ => Say()).ToList(), ct)));
        list.Add(new(MKeys.TplParallelMs, g, $"{parallel} сценария по 200 шагов параллельно", sw.Elapsed.TotalMilliseconds, "мс"));

        // 7. Память длинной последовательности.
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        var (_, _) = await Run([Repeat(1000, Say(), Say(), Say(), Say(), Say())], ct);
        var after = GC.GetTotalMemory(false);
        list.Add(new(MKeys.TplMemMb, g, "Память на 5000 шагов", Math.Max(0, (after - before) / 1048576.0), "МБ", null, "Прирост управляемой кучи"));
        return list;
    }

    private static TemplateStep Say(string? marker = null)
    {
        var s = StepCatalog.Create(StepKind.Say).Set(P.Text, "тест");
        s.Comment = marker;
        return s;
    }

    private static TemplateStep Repeat(int count, params TemplateStep[] body)
    {
        var r = StepCatalog.Create(StepKind.Repeat).Set(P.Count, count.ToString());
        r.Body.AddRange(body);
        return r;
    }

    private static readonly JarvisSettings BenchSettings = new() { StepDelayMs = 0, MaxRepeatCount = 1000 };

    private static TemplateExecutor Executor(StopController stop) =>
        new(new EmptyRepo(), new SandboxRunner(), new SecurityPolicy(() => BenchSettings), new NoUi(), stop, () => BenchSettings);

    private static async Task<(ExecutionResult Result, double Ms)> Run(IReadOnlyList<TemplateStep> steps, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stop = new StopController();
        using var reg = ct.Register(stop.StopAll);
        var sw = Stopwatch.StartNew();
        var r = await Executor(stop).RunStepsAsync("benchmark", steps);
        ct.ThrowIfCancellationRequested();
        return (r, sw.Elapsed.TotalMilliseconds);
    }

    private sealed class SandboxRunner : IStepActionRunner
    {
        private int _n;
        public Task<StepOutcome> ExecuteAsync(TemplateStep step, RunContext ctx, CancellationToken ct) =>
            Task.FromResult(step.Comment == "fail" ? StepOutcome.Fail("тестовая ошибка", retryable: false) : StepOutcome.Ok());
        public Task<bool> EvaluateAsync(StepCondition condition, RunContext ctx, CancellationToken ct) =>
            Task.FromResult(Interlocked.Increment(ref _n) % 2 == 0);
    }

    private sealed class EmptyRepo : ITemplateRepository
    {
        public IReadOnlyList<Template> All => [];
        public Template? Get(Guid id) => null;
        public Template? FindByName(string name) => null;
    }

    private sealed class NoUi : IUserInteraction
    {
        public Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken ct) => Task.FromResult(false);
        public Task<ScreenPoint?> RequestPointAsync(string message, CancellationToken ct) => Task.FromResult<ScreenPoint?>(null);
        public Task<int?> ChooseAsync(string question, IReadOnlyList<string> options, CancellationToken ct) => Task.FromResult<int?>(null);
        public void Notify(string message, NotifyLevel level = NotifyLevel.Info) { }
    }
}
