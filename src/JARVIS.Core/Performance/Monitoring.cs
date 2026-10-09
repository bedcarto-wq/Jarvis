using System.Diagnostics;

namespace Jarvis.Core.Performance;

/// <summary>Снимок мониторинга. null — показатель недоступен (не ноль).</summary>
public sealed record MonitoringSnapshot(DateTime At, double? CpuPercent, double? WorkingSetMb, double? PrivateMb, int? Threads, string ActiveProfile, string CurrentTask);

/// <summary>
/// Лёгкий мониторинг CPU и памяти процесса JARVIS. Отдельно от бенчмарка: никогда его не запускает,
/// включается и выключается пользователем; одно измерение — чтение счётчиков текущего процесса.
/// </summary>
public sealed class MonitoringService : IDisposable
{
    private readonly Func<int> _intervalMs;
    private readonly Func<string> _profile;
    private readonly Func<string> _task;
    private Timer? _timer;
    private TimeSpan _lastCpu;
    private DateTime _lastAt;
    private readonly object _lock = new();

    public MonitoringService(Func<int> intervalMs, Func<string> activeProfile, Func<string> currentTask)
    {
        _intervalMs = intervalMs;
        _profile = activeProfile;
        _task = currentTask;
    }

    public bool IsRunning => _timer is not null;
    public MonitoringSnapshot? Last { get; private set; }
    public event Action<MonitoringSnapshot>? Sampled;

    public void Start()
    {
        lock (_lock)
        {
            if (_timer is not null) return;
            Prime();
            var ms = Math.Clamp(_intervalMs(), 1000, 30000);
            _timer = new Timer(_ => Tick(), null, ms, ms);
        }
    }

    public void Stop()
    {
        lock (_lock) { _timer?.Dispose(); _timer = null; }
    }

    /// <summary>Применяет новый интервал (после смены профиля).</summary>
    public void Reconfigure()
    {
        lock (_lock)
        {
            if (_timer is null) return;
            var ms = Math.Clamp(_intervalMs(), 1000, 30000);
            _timer.Change(ms, ms);
        }
    }

    private void Prime()
    {
        try { using var p = Process.GetCurrentProcess(); _lastCpu = p.TotalProcessorTime; _lastAt = DateTime.UtcNow; }
        catch { _lastAt = default; }
    }

    private void Tick()
    {
        var s = SampleNow();
        Last = s;
        Sampled?.Invoke(s);
    }

    public MonitoringSnapshot SampleNow()
    {
        double? cpu = null, ws = null, priv = null;
        int? threads = null;
        try
        {
            using var p = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var t = p.TotalProcessorTime;
            if (_lastAt != default && now > _lastAt)
                cpu = Math.Clamp((t - _lastCpu).TotalMilliseconds / (now - _lastAt).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100);
            _lastCpu = t;
            _lastAt = now;
            ws = p.WorkingSet64 / 1048576.0;
            priv = p.PrivateMemorySize64 > 0 ? p.PrivateMemorySize64 / 1048576.0 : null;
            threads = p.Threads.Count;
        }
        catch { /* показатели останутся «Недоступно» */ }
        string profile, task;
        try { profile = _profile(); } catch { profile = "—"; }
        try { task = _task(); } catch { task = "—"; }
        return new MonitoringSnapshot(DateTime.Now, cpu, ws, priv, threads, profile, task);
    }

    public void Dispose() => Stop();
}

/// <summary>Ограничитель параллельных фоновых задач (параметр профиля «Параллельных фоновых задач»).</summary>
public sealed class BackgroundGate
{
    private readonly Func<int> _limit;
    private int _active;
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource> _waiters = new();

    public BackgroundGate(Func<int> limit) => _limit = limit;

    public int Active { get { lock (_lock) return _active; } }

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await EnterAsync(ct).ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { Exit(); }
    }

    public async Task RunAsync(Func<Task> work, CancellationToken ct = default) =>
        await RunAsync(async () => { await work().ConfigureAwait(false); return 0; }, ct).ConfigureAwait(false);

    private Task EnterAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_active < Math.Max(1, _limit())) { _active++; return Task.CompletedTask; }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            _waiters.Enqueue(tcs);
            return tcs.Task;
        }
    }

    private void Exit()
    {
        lock (_lock)
        {
            while (_waiters.Count > 0)
            {
                var next = _waiters.Dequeue();
                if (next.TrySetResult()) return; // слот передан следующему
            }
            _active--;
        }
    }
}
