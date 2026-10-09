using System.Diagnostics;
using Jarvis.Core.Performance;
using Jarvis.Core.Text;
using Jarvis.Voice.Recognition;

namespace Jarvis.Voice.Benchmark;

/// <summary>
/// Проверка классического распознавателя PocketSphinx: модель, инициализация, скорость декодирования
/// подготовленного сигнала, число команд в грамматике, (по разрешению) открытие микрофона и —
/// если пользователь положил свои записи с известным текстом — точность на них.
/// Звук с микрофона не записывается и не сохраняется.
/// </summary>
public sealed class VoiceBenchmarkProbe : IBenchmarkProbe
{
    private readonly string _modelDir;
    private readonly string _workDir;
    private readonly Func<GrammarSpec> _grammar;
    private readonly string? _testAudioDir;
    private readonly Func<CancellationToken, Task<string?>>? _microphoneCheck;

    /// <param name="microphoneCheck">Открывает устройство на долю секунды и сразу закрывает; null при успехе или текст ошибки.</param>
    public VoiceBenchmarkProbe(string modelDir, string workDir, Func<GrammarSpec> grammar, string? testAudioDir,
        Func<CancellationToken, Task<string?>>? microphoneCheck)
    {
        _modelDir = modelDir;
        _workDir = workDir;
        _grammar = grammar;
        _testAudioDir = testAudioDir;
        _microphoneCheck = microphoneCheck;
    }

    public string Title => "Голосовой движок (PocketSphinx)";
    public BenchmarkStage Stage => BenchmarkStage.Measuring;

    public async Task<IReadOnlyList<Measurement>> RunAsync(BenchmarkContext ctx, CancellationToken ct)
    {
        const string g = "Распознавание речи";
        var list = new List<Measurement>();

        if (ctx.Request.AllowMicrophoneCheck && _microphoneCheck is not null)
        {
            var err = await _microphoneCheck(ct).ConfigureAwait(false);
            list.Add(err is null
                ? new(MKeys.VoiceMic, g, "Открытие микрофона", 1, "", "открывается", "Устройство открыто и сразу закрыто, звук не сохранялся")
                : Measurement.Unavailable(MKeys.VoiceMic, g, "Открытие микрофона", err));
        }
        else list.Add(Measurement.Unavailable(MKeys.VoiceMic, g, "Открытие микрофона", "Не проверялось: нет разрешения пользователя"));

        var problem = PocketSphinxEngine.ValidateModel(_modelDir);
        if (problem is not null && !problem.Contains("ru.dic"))
        {
            list.Add(Measurement.Unavailable(MKeys.VoiceModel, g, "Русская акустическая модель", problem));
            list.Add(Measurement.Unavailable(MKeys.VoiceRtf, g, "Скорость распознавания", "Модель не установлена"));
            list.Add(Measurement.Unavailable(MKeys.VoiceAccuracy, g, "Точность на тестовых записях", "Модель не установлена"));
            return list;
        }
        list.Add(new(MKeys.VoiceModel, g, "Русская акустическая модель", 1, "", "cmusphinx-ru-5.2 (GMM-HMM, не нейросеть)", problem));

        var p = ctx.CurrentParameters;
        using var engine = new PocketSphinxEngine(_modelDir, _workDir)
        {
            MaxHmmPf = (int)(p.TryGetValue("voice.maxHmmPf", out var m) ? m : 3000),
            BeamExp = (int)(p.TryGetValue("voice.beamExp", out var b) ? b : 30),
        };
        var spec = _grammar();
        using var proc = Process.GetCurrentProcess();
        var mem0 = proc.PrivateMemorySize64;
        var sw = Stopwatch.StartNew();
        try { engine.Initialize(spec); }
        catch (Exception ex)
        {
            list.Add(Measurement.Unavailable(MKeys.VoiceInitMs, g, "Инициализация распознавателя", ex.Message));
            list.Add(Measurement.Unavailable(MKeys.VoiceRtf, g, "Скорость распознавания", "Распознаватель не запустился"));
            list.Add(Measurement.Unavailable(MKeys.VoiceAccuracy, g, "Точность на тестовых записях", "Распознаватель не запустился"));
            return list;
        }
        list.Add(new(MKeys.VoiceInitMs, g, "Инициализация распознавателя", sw.Elapsed.TotalMilliseconds, "мс",
            null, $"maxhmmpf={engine.MaxHmmPf}, beam=1e-{engine.BeamExp}"));
        proc.Refresh();
        if (mem0 > 0) list.Add(new(MKeys.VoiceMemMb, g, "Память распознавателя", Math.Max(0, (proc.PrivateMemorySize64 - mem0) / 1048576.0), "МБ"));
        list.Add(new(MKeys.VoiceCommands, g, "Слов в грамматике команд", spec.AllWords().Count(), "шт."));

        // Скорость: 3 × 2,5 с подготовленного «речеподобного» сигнала (модулированный шум).
        double total = 0, audio = 0;
        for (var i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            var signal = SpeechLikeSignal(40000, i + 1);
            sw.Restart();
            engine.Recognize(signal);
            total += sw.Elapsed.TotalMilliseconds;
            audio += signal.Length / 16.0;
        }
        list.Add(new(MKeys.VoiceDecodeMs, g, "Время обработки 2,5 с аудио", total / 3, "мс", null, "Подготовленный синтетический сигнал"));
        list.Add(new(MKeys.VoiceRtf, g, "Коэффициент реального времени (RTF)", Math.Round(total / audio, 3), "", null,
            "Секунд обработки на 1 с звука; < 0,3 — комфортно. Оценка скорости, а не качества распознавания"));

        // Точность — только на записях пользователя с известным текстом.
        list.Add(MeasureAccuracy(engine, ct));
        return list;
    }

    private Measurement MeasureAccuracy(PocketSphinxEngine engine, CancellationToken ct)
    {
        const string g = "Распознавание речи";
        const string title = "Точность на тестовых записях";
        if (_testAudioDir is null || !Directory.Exists(_testAudioDir))
            return Measurement.Unavailable(MKeys.VoiceAccuracy, g, title, "Нет записей с известным текстом (папка benchmark-audio). Синтезированная речь не подходит для этой модели");
        int total = 0, ok = 0;
        foreach (var wav in Directory.EnumerateFiles(_testAudioDir, "*.wav"))
        {
            ct.ThrowIfCancellationRequested();
            var txt = Path.ChangeExtension(wav, ".txt");
            if (!File.Exists(txt)) continue;
            var samples = WavReader.ReadPcm16Mono16k(wav);
            if (samples is null) continue;
            total++;
            var r = engine.Recognize(samples);
            if (r is not null && TextNormalizer.Normalize(r.Text) == TextNormalizer.Normalize(File.ReadAllText(txt))) ok++;
        }
        return total == 0
            ? Measurement.Unavailable(MKeys.VoiceAccuracy, g, title, "В папке нет пар *.wav (16 кГц, 16 бит, моно) + *.txt")
            : new(MKeys.VoiceAccuracy, g, title, Math.Round(100.0 * ok / total, 1), "%", $"{ok} из {total}",
                "Только для этих записей; не гарантирует качество с другим микрофоном и в другой обстановке");
    }

    /// <summary>Шум с огибающей «слогов» 4 Гц — нагружает декодер так же, как речь (по числу активных HMM).</summary>
    public static short[] SpeechLikeSignal(int samples, int seed)
    {
        var rnd = new Random(seed);
        var s = new short[samples];
        double lp = 0;
        for (var i = 0; i < samples; i++)
        {
            var env = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * i / 16000.0);
            lp = 0.7 * lp + 0.3 * (rnd.NextDouble() * 2 - 1);
            var tone = Math.Sin(2 * Math.PI * (180 + 40 * Math.Sin(i / 3000.0)) * i / 16000.0);
            s[i] = (short)Math.Clamp((lp * 0.6 + tone * 0.4) * env * 9000, short.MinValue, short.MaxValue);
        }
        return s;
    }
}

public static class WavReader
{
    /// <summary>Читает WAV PCM 16 бит, моно, 16 кГц. Другие форматы — null.</summary>
    public static short[]? ReadPcm16Mono16k(string path)
    {
        try
        {
            using var br = new BinaryReader(File.OpenRead(path));
            if (new string(br.ReadChars(4)) != "RIFF") return null;
            br.ReadInt32();
            if (new string(br.ReadChars(4)) != "WAVE") return null;
            short channels = 0, bits = 0; int rate = 0;
            while (br.BaseStream.Position < br.BaseStream.Length - 8)
            {
                var id = new string(br.ReadChars(4));
                var size = br.ReadInt32();
                if (id == "fmt ")
                {
                    var fmt = br.ReadInt16(); channels = br.ReadInt16(); rate = br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                    if (size > 16) br.ReadBytes(size - 16);
                    if (fmt != 1) return null;
                }
                else if (id == "data")
                {
                    if (channels != 1 || bits != 16 || rate != 16000) return null;
                    var bytes = br.ReadBytes(size);
                    var res = new short[bytes.Length / 2];
                    Buffer.BlockCopy(bytes, 0, res, 0, res.Length * 2);
                    return res;
                }
                else br.ReadBytes(size);
            }
        }
        catch { }
        return null;
    }
}
