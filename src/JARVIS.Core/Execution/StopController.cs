using Jarvis.Core.Abstractions;

namespace Jarvis.Core.Execution;

/// <summary>Асинхронный «шлюз» паузы: выполнение ждёт, пока пауза не снята.</summary>
public sealed class PauseGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _tcs = CreateCompleted();

    public bool IsPaused { get; private set; }
    public event Action<bool>? Changed;

    private static TaskCompletionSource CreateCompleted()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (IsPaused) return;
            IsPaused = true;
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Changed?.Invoke(true);
    }

    public void Resume()
    {
        TaskCompletionSource t;
        lock (_lock)
        {
            if (!IsPaused) return;
            IsPaused = false;
            t = _tcs;
        }
        t.TrySetResult();
        Changed?.Invoke(false);
    }

    public Task WaitAsync(CancellationToken ct)
    {
        Task t;
        lock (_lock) t = _tcs.Task;
        return t.IsCompleted ? Task.CompletedTask : t.WaitAsync(ct);
    }
}

/// <summary>
/// Центральная остановка. Стоп имеет приоритет над всем: отменяет текущий сценарий,
/// ожидающие подтверждения и отпускает удерживаемые клавиши.
/// </summary>
public sealed class StopController
{
    private readonly object _lock = new();
    private readonly IInputService? _input;
    private CancellationTokenSource _runCts = new();
    private CancellationTokenSource _confirmCts = new();

    public StopController(IInputService? input = null) => _input = input;

    public PauseGate Pause { get; } = new();
    public event Action? Stopped;

    public CancellationToken RunToken
    {
        get { lock (_lock) return _runCts.Token; }
    }

    public CancellationToken ConfirmationToken
    {
        get { lock (_lock) return _confirmCts.Token; }
    }

    /// <summary>Новый токен для запускаемого сценария (если предыдущий был остановлен).</summary>
    public CancellationToken BeginRun()
    {
        lock (_lock)
        {
            if (_runCts.IsCancellationRequested)
            {
                _runCts.Dispose();
                _runCts = new CancellationTokenSource();
            }
            if (_confirmCts.IsCancellationRequested)
            {
                _confirmCts.Dispose();
                _confirmCts = new CancellationTokenSource();
            }
            return _runCts.Token;
        }
    }

    public void StopAll()
    {
        CancellationTokenSource run, confirm;
        lock (_lock)
        {
            run = _runCts;
            confirm = _confirmCts;
        }
        // Сначала сценарий, затем подтверждение: ожидающий шаг увидит именно остановку.
        try { run.Cancel(); } catch (ObjectDisposedException) { }
        try { confirm.Cancel(); } catch (ObjectDisposedException) { }
        Pause.Resume();
        try { _input?.ReleaseAll(); } catch { /* отпускание клавиш не должно падать */ }
        Stopped?.Invoke();
    }

    public void CancelPendingConfirmation()
    {
        CancellationTokenSource confirm;
        lock (_lock)
        {
            confirm = _confirmCts;
            _confirmCts = new CancellationTokenSource();
        }
        try { confirm.Cancel(); } catch (ObjectDisposedException) { }
    }
}
