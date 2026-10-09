using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jarvis.Core.Performance;

/// <summary>Платформенно-независимые сведения о системе (на Windows дополняются точными данными из JARVIS.Windows).</summary>
public sealed class RuntimeSystemProbe(string dataRoot) : IBenchmarkProbe
{
    public string Title => "Сведения о системе (.NET)";
    public BenchmarkStage Stage => BenchmarkStage.SystemInfo;

    public Task<IReadOnlyList<Measurement>> RunAsync(BenchmarkContext ctx, CancellationToken ct)
    {
        const string g = "Система";
        var list = new List<Measurement>
        {
            new(MKeys.LogicalCpus, g, "Логических процессоров", Environment.ProcessorCount, "шт."),
            new(MKeys.Arch, g, "Архитектура", null, "", RuntimeInformation.OSArchitecture.ToString()),
        };
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dataRoot));
            if (root is not null)
            {
                var di = new DriveInfo(root);
                list.Add(new(MKeys.DiskFreeGb, g, $"Свободно на диске с данными ({root})", di.AvailableFreeSpace / 1073741824.0, "ГБ"));
            }
        }
        catch (Exception ex) { list.Add(Measurement.Unavailable(MKeys.DiskFreeGb, g, "Свободно на диске с данными", ex.Message)); }
        return Task.FromResult<IReadOnlyList<Measurement>>(list);
    }
}

/// <summary>Нагрузка самого JARVIS в режиме ожидания (CPU процесса, а не всего компьютера).</summary>
public sealed class SelfLoadProbe : IBenchmarkProbe
{
    public string Title => "Нагрузка самого JARVIS";
    public BenchmarkStage Stage => BenchmarkStage.Measuring;
    public int QuietPeriodMs { get; init; } = 2000;

    public async Task<IReadOnlyList<Measurement>> RunAsync(BenchmarkContext ctx, CancellationToken ct)
    {
        const string g = "Нагрузка JARVIS";
        using var p = Process.GetCurrentProcess();
        var cpu0 = p.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        await Task.Delay(QuietPeriodMs, ct).ConfigureAwait(false);
        p.Refresh();
        var cpuPct = (p.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        return
        [
            new(MKeys.SelfCpuIdle, g, "CPU процесса JARVIS в ожидании", Math.Round(cpuPct, 2), "%", null,
                $"За {QuietPeriodMs / 1000.0:0.#} с паузы бенчмарка: включает прослушивание микрофона, HUD и сам бенчмарк в покое — оценка сверху."),
            new(MKeys.SelfWorkingSetMb, g, "Рабочий набор памяти", p.WorkingSet64 / 1048576.0, "МБ"),
            p.PrivateMemorySize64 > 0 ? new(MKeys.SelfPrivateMb, g, "Частная память", p.PrivateMemorySize64 / 1048576.0, "МБ")
                                      : Measurement.Unavailable(MKeys.SelfPrivateMb, g, "Частная память", "ОС не сообщает значение"),
            new(MKeys.SelfGcHeapMb, g, "Управляемая куча .NET", GC.GetTotalMemory(false) / 1048576.0, "МБ"),
            new(MKeys.SelfThreads, g, "Потоков", p.Threads.Count, "шт."),
        ];
    }
}

/// <summary>
/// Задержки чтения/записи в собственное хранилище JARVIS. Небольшие файлы (≤ 1 МБ),
/// которые сразу удаляются. Не стресс-тест и не тест диска целиком.
/// </summary>
public sealed class StorageProbe(string dataDir, Func<string?> extraDir) : IBenchmarkProbe
{
    public string Title => "Скорость хранилища данных";
    public BenchmarkStage Stage => BenchmarkStage.Measuring;

    public async Task<IReadOnlyList<Measurement>> RunAsync(BenchmarkContext ctx, CancellationToken ct)
    {
        const string g = "Хранилище";
        var list = new List<Measurement>();
        var (small, write, read) = await MeasureAsync(dataDir, ct).ConfigureAwait(false);
        list.Add(new(MKeys.DiskSmallWriteMs, g, "Запись небольшого файла настроек (4 КБ)", small, "мс", null, "Среднее по 10 записям со сбросом на диск"));
        list.Add(new(MKeys.DiskWriteMs, g, "Запись 1 МБ", write, "мс"));
        list.Add(new(MKeys.DiskReadMs, g, "Чтение 1 МБ", read, "мс", null, "Может обслуживаться из кэша Windows"));
        var extra = extraDir();
        if (!string.IsNullOrWhiteSpace(extra))
        {
            var (ok, msg) = StorageLocations.Probe(extra);
            if (ok)
            {
                var (s2, _, _) = await MeasureAsync(extra, ct).ConfigureAwait(false);
                list.Add(new(MKeys.DiskExtra, g, $"Запись в дополнительное хранилище ({extra})", s2, "мс"));
            }
            else list.Add(Measurement.Unavailable(MKeys.DiskExtra, g, "Дополнительное хранилище", msg));
        }
        return list;
    }

    private static async Task<(double Small, double Write, double Read)> MeasureAsync(string dir, CancellationToken ct)
    {
        var bench = Path.Combine(dir, ".bench-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bench);
        try
        {
            var smallData = new byte[4096];
            var big = new byte[1 << 20];
            new Random(1).NextBytes(big);
            var sw = new Stopwatch();
            double small = 0;
            for (var i = 0; i < 10; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = Path.Combine(bench, $"s{i}.json");
                sw.Restart();
                await using (var fs = new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
                    await fs.WriteAsync(smallData, ct).ConfigureAwait(false);
                small += sw.Elapsed.TotalMilliseconds;
            }
            var bf = Path.Combine(bench, "big.bin");
            sw.Restart();
            await using (var fs = new FileStream(bf, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough | FileOptions.Asynchronous))
                await fs.WriteAsync(big, ct).ConfigureAwait(false);
            var write = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            _ = await File.ReadAllBytesAsync(bf, ct).ConfigureAwait(false);
            var read = sw.Elapsed.TotalMilliseconds;
            return (small / 10, write, read);
        }
        finally
        {
            try { Directory.Delete(bench, true); } catch { }
        }
    }
}
