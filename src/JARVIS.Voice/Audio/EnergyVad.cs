namespace Jarvis.Voice.Audio;

/// <summary>
/// Классический энергетический детектор речи: RMS кадра в дБFS, порог, «предзапись»
/// перед началом речи и тайм-аут тишины в конце фразы. Без нейросетей.
/// </summary>
public sealed class EnergyVad
{
    public const int FrameSamples = 320; // 20 мс при 16 кГц
    private const int FrameMs = 20;
    private const int StartFrames = 3;   // 60 мс речи подряд для старта
    private const int PrerollFrames = 15; // 300 мс до начала речи
    private const int MinSpeechMs = 250;

    private readonly Queue<short[]> _preroll = new();
    private readonly List<short> _utterance = new(16000 * 4);
    private readonly short[] _frame = new short[FrameSamples];
    private int _frameFill;
    private int _aboveCount;
    private int _silenceMs;
    private int _speechMs;

    public double ThresholdDb { get; set; } = -42;
    public int EndSilenceMs { get; set; } = 800;
    public int MaxUtteranceMs { get; set; } = 12_000;
    public bool InSpeech { get; private set; }
    public double LastLevelDb { get; private set; } = -100;

    public event Action? SpeechStarted;
    public event Action<short[]>? UtteranceCompleted;
    public event Action<double>? Level;

    public void Reset()
    {
        _preroll.Clear();
        _utterance.Clear();
        _frameFill = 0;
        _aboveCount = 0;
        _silenceMs = 0;
        _speechMs = 0;
        InSpeech = false;
    }

    public void Process(short[] samples, int count)
    {
        int i = 0;
        while (i < count)
        {
            int take = Math.Min(FrameSamples - _frameFill, count - i);
            Array.Copy(samples, i, _frame, _frameFill, take);
            _frameFill += take;
            i += take;
            if (_frameFill == FrameSamples)
            {
                ProcessFrame(_frame);
                _frameFill = 0;
            }
        }
    }

    public static double RmsDb(ReadOnlySpan<short> frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += (double)s * s;
        double rms = Math.Sqrt(sum / Math.Max(1, frame.Length));
        return rms < 1 ? -100 : 20 * Math.Log10(rms / 32768.0);
    }

    private void ProcessFrame(short[] frame)
    {
        double db = RmsDb(frame);
        LastLevelDb = db;
        Level?.Invoke(db);
        bool loud = db >= ThresholdDb;

        if (!InSpeech)
        {
            var copy = (short[])frame.Clone();
            _preroll.Enqueue(copy);
            while (_preroll.Count > PrerollFrames) _preroll.Dequeue();
            _aboveCount = loud ? _aboveCount + 1 : 0;
            if (_aboveCount >= StartFrames)
            {
                InSpeech = true;
                _utterance.Clear();
                foreach (var f in _preroll) _utterance.AddRange(f);
                _preroll.Clear();
                _silenceMs = 0;
                _speechMs = StartFrames * FrameMs;
                SpeechStarted?.Invoke();
            }
            return;
        }

        _utterance.AddRange(frame);
        if (loud)
        {
            _silenceMs = 0;
            _speechMs += FrameMs;
        }
        else
        {
            _silenceMs += FrameMs;
        }
        int totalMs = _utterance.Count / (IAudioSource.SampleRate / 1000);
        // Пока пользователь говорит — продолжаем слушать; фраза завершается только тишиной (или лимитом длины).
        if (_silenceMs >= EndSilenceMs || totalMs >= MaxUtteranceMs)
        {
            InSpeech = false;
            _aboveCount = 0;
            var data = _utterance.ToArray();
            _utterance.Clear();
            if (_speechMs >= MinSpeechMs) UtteranceCompleted?.Invoke(data);
        }
    }
}
