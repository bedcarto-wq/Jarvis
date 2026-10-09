using System.Diagnostics;
using System.Runtime.InteropServices;
using Jarvis.Core.Performance;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Jarvis.Platform;

/// <summary>Сведения о Windows-компьютере через системные API (реестр, GlobalMemoryStatusEx, GetSystemTimes, IOCTL).</summary>
public sealed class WindowsSystemProbe(string dataRoot) : IBenchmarkProbe
{
    public string Title => "Сведения о системе (Windows)";
    public BenchmarkStage Stage => BenchmarkStage.SystemInfo;

    public async Task<IReadOnlyList<Measurement>> RunAsync(BenchmarkContext ctx, CancellationToken ct)
    {
        const string g = "Система";
        var list = new List<Measurement>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = (k?.GetValue("ProcessorNameString") as string)?.Trim();
            list.Add(name is null ? Measurement.Unavailable(MKeys.CpuName, g, "Процессор", "Не указан в реестре") : new(MKeys.CpuName, g, "Процессор", null, "", name));
        }
        catch (Exception ex) { list.Add(Measurement.Unavailable(MKeys.CpuName, g, "Процессор", ex.Message)); }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            list.Add(new(MKeys.RamTotalMb, g, "Оперативная память всего", mem.ullTotalPhys / 1048576.0, "МБ"));
            list.Add(new(MKeys.RamAvailableMb, g, "Оперативная память свободно", mem.ullAvailPhys / 1048576.0, "МБ"));
        }
        else
        {
            list.Add(Measurement.Unavailable(MKeys.RamTotalMb, g, "Оперативная память всего", "GlobalMemoryStatusEx не сработал"));
            list.Add(Measurement.Unavailable(MKeys.RamAvailableMb, g, "Оперативная память свободно", "GlobalMemoryStatusEx не сработал"));
        }

        // Загрузка CPU всем компьютером за 1 с (не только JARVIS).
        if (GetSystemTimes(out var i0, out var k0, out var u0))
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (GetSystemTimes(out var i1, out var k1, out var u1))
            {
                double idle = i1 - i0, total = (k1 - k0) + (u1 - u0);
                list.Add(new(MKeys.CpuLoad, g, "Загрузка CPU компьютером", total > 0 ? Math.Round((1 - idle / total) * 100, 1) : 0, "%", null, "Все процессы за 1 с"));
            }
        }
        else list.Add(Measurement.Unavailable(MKeys.CpuLoad, g, "Загрузка CPU компьютером", "GetSystemTimes не сработал"));

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = k?.GetValue("ProductName") as string;
            var display = k?.GetValue("DisplayVersion") as string;
            var build = k?.GetValue("CurrentBuildNumber") as string;
            var ubr = k?.GetValue("UBR");
            // В Windows 11 ProductName по-прежнему «Windows 10 …»; редакцию уточняем по номеру сборки.
            if (product is not null && int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            list.Add(new(MKeys.Os, g, "Windows", null, "", $"{product} {display} (сборка {build}.{ubr})"));
        }
        catch (Exception ex) { list.Add(Measurement.Unavailable(MKeys.Os, g, "Windows", ex.Message)); }

        try
        {
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            var ps = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
            list.Add(new(MKeys.Screen, g, "Экран", null, "", $"основной {ps?.Width}×{ps?.Height}, мониторов: {System.Windows.Forms.Screen.AllScreens.Length}, рабочий стол {vs.Width}×{vs.Height}"));
        }
        catch (Exception ex) { list.Add(Measurement.Unavailable(MKeys.Screen, g, "Экран", ex.Message)); }

        list.Add(DiskKind(dataRoot));
        return list;
    }

    /// <summary>
    /// Тип диска по признаку IncursSeekPenalty (IOCTL_STORAGE_QUERY_PROPERTY). Если драйвер
    /// не сообщает — «Недоступно»; по букве диска или модели тип не угадывается.
    /// </summary>
    private static Measurement DiskKind(string path)
    {
        const string g = "Система";
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path))!.TrimEnd('\\');
            using var h = CreateFileW(@"\\.\" + root, 0, 3, 0, 3, 0, 0);
            if (h.IsInvalid) return Measurement.Unavailable(MKeys.DiskKind, g, "Тип диска с данными", "Нет доступа к тому");
            var query = new STORAGE_PROPERTY_QUERY { PropertyId = 7 /* StorageDeviceSeekPenaltyProperty */, QueryType = 0 };
            var outBuf = new DEVICE_SEEK_PENALTY_DESCRIPTOR();
            if (!DeviceIoControl(h, 0x2D1400, ref query, Marshal.SizeOf(query), ref outBuf, Marshal.SizeOf(outBuf), out _, 0))
                return Measurement.Unavailable(MKeys.DiskKind, g, "Тип диска с данными", "Драйвер не сообщает (виртуальный диск, RAID, USB)");
            return new(MKeys.DiskKind, g, "Тип диска с данными", outBuf.IncursSeekPenalty ? 1 : 0, "",
                outBuf.IncursSeekPenalty ? "HDD (есть задержка позиционирования)" : "SSD (без задержки позиционирования)");
        }
        catch (Exception ex) { return Measurement.Unavailable(MKeys.DiskKind, g, "Тип диска с данными", ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY { public int PropertyId; public int QueryType; public byte AdditionalParameters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR { public uint Version; public uint Size; [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint sec, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref STORAGE_PROPERTY_QUERY inBuf, int inSize,
        ref DEVICE_SEEK_PENALTY_DESCRIPTOR outBuf, int outSize, out int returned, nint overlapped);
}
