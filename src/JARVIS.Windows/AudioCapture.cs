using Jarvis.Voice.Audio;
using NAudio.Wave;

namespace Jarvis.Platform;

/// <summary>Захват микрофона (WinMM, 16 кГц моно 16 бит). Данные только в памяти.</summary>
public sealed class AudioCapture : IAudioSource
{
    private WaveInEvent? _wave;
    private readonly object _lock = new();

    public event Action<short[], int>? SamplesAvailable;
    public event Action<string>? Error;
    public bool IsRunning => _wave is not null;

    public IReadOnlyList<AudioDevice> GetDevices()
    {
        var list = new List<AudioDevice>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try { list.Add(new AudioDevice(i, WaveInEvent.GetCapabilities(i).ProductName)); }
            catch { list.Add(new AudioDevice(i, $"Микрофон {i + 1}")); }
        }
        return list;
    }

    public void Start(int deviceNumber)
    {
        lock (_lock)
        {
            Stop();
            if (WaveInEvent.DeviceCount == 0) { Error?.Invoke("Микрофон не найден"); return; }
            if (deviceNumber < 0 || deviceNumber >= WaveInEvent.DeviceCount) deviceNumber = 0;
            var w = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(IAudioSource.SampleRate, 16, 1),
                BufferMilliseconds = 40,
                NumberOfBuffers = 4,
            };
            w.DataAvailable += (_, e) =>
            {
                var n = e.BytesRecorded / 2;
                var buf = new short[n];
                Buffer.BlockCopy(e.Buffer, 0, buf, 0, n * 2);
                SamplesAvailable?.Invoke(buf, n);
            };
            w.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Error?.Invoke($"Запись остановлена: {e.Exception.Message}");
            };
            try
            {
                w.StartRecording();
                _wave = w;
            }
            catch (Exception ex)
            {
                w.Dispose();
                Error?.Invoke($"Не удалось открыть микрофон: {ex.Message}. Проверьте «Параметры → Конфиденциальность → Микрофон».");
            }
        }
    }

    /// <summary>
    /// Проверяет, что устройство открывается: запуск на ~300 мс и закрытие. Данные отбрасываются,
    /// ничего не сохраняется. null — успех, иначе текст ошибки.
    /// </summary>
    public async Task<string?> CheckDeviceAsync(int deviceNumber, CancellationToken ct)
    {
        if (IsRunning) return null; // уже открыто JARVIS — значит, открывается
        if (WaveInEvent.DeviceCount == 0) return "Микрофон не найден";
        if (deviceNumber < 0 || deviceNumber >= WaveInEvent.DeviceCount) deviceNumber = 0;
        using var w = new WaveInEvent { DeviceNumber = deviceNumber, WaveFormat = new WaveFormat(IAudioSource.SampleRate, 16, 1), BufferMilliseconds = 50 };
        var got = 0;
        w.DataAvailable += (_, e) => Interlocked.Add(ref got, e.BytesRecorded);
        try
        {
            w.StartRecording();
            await Task.Delay(300, ct);
            w.StopRecording();
            return got > 0 ? null : "Устройство открылось, но не передаёт данные";
        }
        catch (OperationCanceledException) { try { w.StopRecording(); } catch { } throw; }
        catch (Exception ex) { return $"Не удалось открыть микрофон: {ex.Message}"; }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_wave is null) return;
            try { _wave.StopRecording(); } catch { /* устройство отключено */ }
            _wave.Dispose();
            _wave = null;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>Короткие синтезированные звуковые сигналы (без файлов).</summary>
public sealed class Earcons : IEarcons
{
    public bool Enabled { get; set; } = true;

    public void ListenStart() => Play([(880, 70), (1320, 90)]);
    public void CommandDone() => Play([(1320, 60), (990, 80)]);
    public void Error() => Play([(330, 160), (250, 200)]);

    private void Play((int Hz, int Ms)[] tones)
    {
        if (!Enabled) return;
        Task.Run(() =>
        {
            try
            {
                const int rate = 22050;
                var samples = new List<short>();
                foreach (var (hz, ms) in tones)
                {
                    var n = rate * ms / 1000;
                    for (var i = 0; i < n; i++)
                    {
                        var env = Math.Min(1.0, Math.Min(i, n - i) / (rate * 0.008));
                        samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 6000 * env));
                    }
                }
                var bytes = new byte[samples.Count * 2];
                Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
                using var ms2 = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(rate, 16, 1));
                using var outDev = new WaveOutEvent();
                outDev.Init(ms2);
                outDev.Play();
                while (outDev.PlaybackState == PlaybackState.Playing) Thread.Sleep(20);
            }
            catch { /* нет устройства вывода — сигнал не критичен */ }
        });
    }
}
