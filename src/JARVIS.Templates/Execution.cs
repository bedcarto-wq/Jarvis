using System.Diagnostics;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Execution;
using Jarvis.Core.Logging;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;

namespace Jarvis.Templates;

public enum ExecutionStatus { Completed, Failed, Stopped, Cancelled, Busy }

public sealed record ExecutionResult(ExecutionStatus Status, string Message, int StepsExecuted);

public enum ExecutionEventKind
{
    Started, StepStarted, StepSucceeded, StepFailed, StepRetry, AwaitingConfirmation, Paused, Resumed,
    Completed, Failed, Stopped, Cancelled,
}

public sealed record ExecutionEvent(ExecutionEventKind Kind, string TemplateName, string? Step, string? Message, int Depth);

public sealed record StepOutcome(bool Success, string Message, bool Retryable = true)
{
    public static StepOutcome Ok(string message = "Готово") => new(true, message);
    public static StepOutcome Fail(string message, bool retryable = true) => new(false, message, retryable);
}

/// <summary>Состояние одного запуска.</summary>
public sealed class RunContext
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public Stack<Guid> CallStack { get; } = new();
    public bool LastSuccess { get; set; } = true;
    public ScreenRect? LastFound { get; set; }
    public TimeSpan Elapsed => _sw.Elapsed;
    public int StepsExecuted { get; set; }
    public void RestartTimer() => _sw.Restart();
}

/// <summary>Исполнитель отдельных действий (окна, ввод, зрение, файлы).</summary>
public interface IStepActionRunner
{
    Task<StepOutcome> ExecuteAsync(TemplateStep step, RunContext ctx, CancellationToken ct);
    Task<bool> EvaluateAsync(StepCondition condition, RunContext ctx, CancellationToken ct);
}

internal sealed class EndTemplateSignal : Exception;

internal sealed class StepFailedException(string message, bool cancelled = false) : Exception(message)
{
    public bool Cancelled { get; } = cancelled;
}

/// <summary>
/// Асинхронный интерпретатор шаблонов: последовательности, условия, повторы с лимитом,
/// вложенные шаблоны с защитой от циклов, пауза, стоп и подтверждение опасных действий.
/// </summary>
public sealed class TemplateExecutor
{
    private readonly ITemplateRepository _repo;
    private readonly IStepActionRunner _runner;
    private readonly SecurityPolicy _security;
    private readonly IUserInteraction _ui;
    private readonly StopController _stop;
    private readonly Func<JarvisSettings> _settings;
    private readonly IDelay _delay;
    private readonly IJarvisLog _log;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public TemplateExecutor(ITemplateRepository repo, IStepActionRunner runner, SecurityPolicy security,
        IUserInteraction ui, StopController stop, Func<JarvisSettings> settings, IDelay? delay = null, IJarvisLog? log = null)
    {
        _repo = repo;
        _runner = runner;
        _security = security;
        _ui = ui;
        _stop = stop;
        _settings = settings;
        _delay = delay ?? RealDelay.Instance;
        _log = log ?? NullLog.Instance;
    }

    public event Action<ExecutionEvent>? Event;
    public bool IsRunning { get; private set; }
    public string? CurrentName { get; private set; }

    public Task<ExecutionResult> RunAsync(Template template) =>
        RunCoreAsync(template.Name, ctx => RunTemplateBodyAsync(template, ctx, _stop.RunToken, 0));

    /// <summary>Выполняет произвольный список шагов (используется голосовыми командами).</summary>
    public Task<ExecutionResult> RunStepsAsync(string name, IReadOnlyList<TemplateStep> steps) =>
        RunCoreAsync(name, ctx => ExecuteListAsync(name, steps, ctx, _stop.RunToken, 0));

    private async Task<ExecutionResult> RunCoreAsync(string name, Func<RunContext, Task> body)
    {
        if (!await _runLock.WaitAsync(0))
            return new ExecutionResult(ExecutionStatus.Busy, $"Уже выполняется «{CurrentName}».", 0);
        var ctx = new RunContext();
        _stop.BeginRun();
        IsRunning = true;
        CurrentName = name;
        Raise(ExecutionEventKind.Started, name, null, null, 0);
        try
        {
            await body(ctx);
            Raise(ExecutionEventKind.Completed, name, null, "Готово", 0);
            return new ExecutionResult(ExecutionStatus.Completed, "Готово", ctx.StepsExecuted);
        }
        catch (OperationCanceledException)
        {
            Raise(ExecutionEventKind.Stopped, name, null, "Остановлено", 0);
            return new ExecutionResult(ExecutionStatus.Stopped, "Остановлено", ctx.StepsExecuted);
        }
        catch (StepFailedException ex) when (ex.Cancelled)
        {
            Raise(ExecutionEventKind.Cancelled, name, null, ex.Message, 0);
            return new ExecutionResult(ExecutionStatus.Cancelled, ex.Message, ctx.StepsExecuted);
        }
        catch (StepFailedException ex)
        {
            Raise(ExecutionEventKind.Failed, name, null, ex.Message, 0);
            return new ExecutionResult(ExecutionStatus.Failed, ex.Message, ctx.StepsExecuted);
        }
        catch (Exception ex)
        {
            _log.Error($"Ошибка выполнения «{name}»: {ex}");
            Raise(ExecutionEventKind.Failed, name, null, ex.Message, 0);
            return new ExecutionResult(ExecutionStatus.Failed, "Ошибка: " + ex.Message, ctx.StepsExecuted);
        }
        finally
        {
            IsRunning = false;
            CurrentName = null;
            _runLock.Release();
        }
    }

    private async Task RunTemplateBodyAsync(Template t, RunContext ctx, CancellationToken ct, int depth)
    {
        if (ctx.CallStack.Contains(t.Id))
            throw new StepFailedException($"Циклический вызов шаблона «{t.Name}» заблокирован.");
        if (depth >= _settings().MaxTemplateNesting)
            throw new StepFailedException($"Превышена глубина вложенности шаблонов ({_settings().MaxTemplateNesting}).");
        ctx.CallStack.Push(t.Id);
        try
        {
            await ExecuteListAsync(t.Name, t.Steps, ctx, ct, depth);
        }
        catch (EndTemplateSignal)
        {
            // «Завершение шаблона» завершает только текущий шаблон.
        }
        finally
        {
            ctx.CallStack.Pop();
        }
    }

    private async Task ExecuteListAsync(string name, IReadOnlyList<TemplateStep> steps, RunContext ctx, CancellationToken ct, int depth)
    {
        foreach (var step in steps)
        {
            if (!step.Enabled) continue;
            await CheckpointAsync(name, ct, depth);
            await ExecuteStepAsync(name, step, ctx, ct, depth);
            var delay = _settings().StepDelayMs;
            if (delay > 0) await _delay.Delay(delay, ct);
        }
    }

    private async Task CheckpointAsync(string name, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        if (_stop.Pause.IsPaused)
        {
            Raise(ExecutionEventKind.Paused, name, null, "Приостановлено", depth);
            await _stop.Pause.WaitAsync(ct);
            Raise(ExecutionEventKind.Resumed, name, null, "Продолжаю", depth);
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task ExecuteStepAsync(string name, TemplateStep step, RunContext ctx, CancellationToken ct, int depth)
    {
        var desc = StepCatalog.Describe(step);
        switch (step.Kind)
        {
            case StepKind.If:
            {
                Raise(ExecutionEventKind.StepStarted, name, desc, null, depth);
                bool ok = step.Condition is not null && await EvaluateAsync(step.Condition, ctx, ct);
                Raise(ExecutionEventKind.StepSucceeded, name, desc, ok ? "Условие выполнено" : "Условие не выполнено", depth);
                await ExecuteListAsync(name, ok ? step.Then : step.Else, ctx, ct, depth);
                return;
            }
            case StepKind.Repeat:
            {
                var s = _settings();
                int count = Math.Clamp(step.GetInt(P.Count, 1), 1, s.MaxRepeatCount);
                Raise(ExecutionEventKind.StepStarted, name, desc, $"Повторов: {count}", depth);
                for (int i = 0; i < count; i++)
                {
                    await CheckpointAsync(name, ct, depth);
                    if (step.Condition is not null && await EvaluateAsync(step.Condition, ctx, ct)) break;
                    await ExecuteListAsync(name, step.Body, ctx, ct, depth);
                }
                Raise(ExecutionEventKind.StepSucceeded, name, desc, null, depth);
                return;
            }
            case StepKind.RunTemplate:
            {
                var target = step.Get(P.Template) is { } n ? _repo.FindByName(n) : null;
                if (target is null)
                {
                    HandleFailure(name, step, desc, ctx, $"Шаблон «{step.Get(P.Template)}» не найден.", depth);
                    return;
                }
                Raise(ExecutionEventKind.StepStarted, name, desc, null, depth);
                await RunTemplateBodyAsync(target, ctx, ct, depth + 1);
                ctx.StepsExecuted++;
                return;
            }
            case StepKind.EndTemplate:
                Raise(ExecutionEventKind.StepSucceeded, name, desc, null, depth);
                throw new EndTemplateSignal();
            case StepKind.Wait:
            {
                Raise(ExecutionEventKind.StepStarted, name, desc, null, depth);
                int remaining = Math.Clamp(step.GetInt(P.Ms, 1000), 0, 3_600_000);
                // Ожидание кусками, чтобы реагировать на стоп и паузу.
                while (remaining > 0)
                {
                    await CheckpointAsync(name, ct, depth);
                    int chunk = Math.Min(250, remaining);
                    await _delay.Delay(chunk, ct);
                    remaining -= chunk;
                }
                ctx.LastSuccess = true;
                ctx.StepsExecuted++;
                Raise(ExecutionEventKind.StepSucceeded, name, desc, null, depth);
                return;
            }
        }

        // Обычное действие: подтверждение опасного, повторы с ограничением.
        var category = StepCatalog.GetDangerCategory(step, _security);
        if (_security.RequiresConfirmation(category))
        {
            Raise(ExecutionEventKind.AwaitingConfirmation, name, desc, category.ToRussian(), depth);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.ConfirmationToken);
            bool confirmed;
            try
            {
                confirmed = await _ui.ConfirmAsync(new ConfirmationRequest(desc, category), linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                confirmed = false;
            }
            ct.ThrowIfCancellationRequested();
            if (!confirmed) throw new StepFailedException($"Действие «{desc}» отменено пользователем.", cancelled: true);
        }

        var settings = _settings();
        int attempts = Math.Clamp(step.Retries ?? settings.DefaultRetries, 1, settings.MaxRetriesCap);
        Raise(ExecutionEventKind.StepStarted, name, desc, null, depth);
        StepOutcome outcome = StepOutcome.Fail("Не выполнено");
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            await CheckpointAsync(name, ct, depth);
            try
            {
                outcome = await _runner.ExecuteAsync(step, ctx, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"Шаг «{desc}»: {ex.Message}");
                outcome = StepOutcome.Fail(ex.Message);
            }
            if (outcome.Success || !outcome.Retryable) break;
            if (attempt < attempts)
            {
                Raise(ExecutionEventKind.StepRetry, name, desc, $"Попытка {attempt + 1} из {attempts}: {outcome.Message}", depth);
                await _delay.Delay(400, ct);
            }
        }
        ctx.StepsExecuted++;
        if (outcome.Success)
        {
            ctx.LastSuccess = true;
            Raise(ExecutionEventKind.StepSucceeded, name, desc, outcome.Message, depth);
        }
        else
        {
            HandleFailure(name, step, desc, ctx, outcome.Message, depth);
        }
    }

    private void HandleFailure(string name, TemplateStep step, string desc, RunContext ctx, string message, int depth)
    {
        ctx.LastSuccess = false;
        Raise(ExecutionEventKind.StepFailed, name, desc, message, depth);
        if (!step.ContinueOnError) throw new StepFailedException($"{desc}: {message}");
    }

    private async Task<bool> EvaluateAsync(StepCondition c, RunContext ctx, CancellationToken ct)
    {
        bool r = c.Kind switch
        {
            ConditionKind.PreviousStepSucceeded => ctx.LastSuccess,
            ConditionKind.TimeoutElapsed => ctx.Elapsed.TotalMilliseconds >=
                                            (int.TryParse(c.Parameters.GetValueOrDefault(P.Ms), out var ms) ? ms : 0),
            _ => await _runner.EvaluateAsync(c, ctx, ct),
        };
        return c.Negate ? !r : r;
    }

    private void Raise(ExecutionEventKind kind, string name, string? step, string? msg, int depth)
    {
        try { Event?.Invoke(new ExecutionEvent(kind, name, step, msg, depth)); }
        catch (Exception ex) { _log.Warn("Обработчик события выполнения: " + ex.Message); }
    }
}
