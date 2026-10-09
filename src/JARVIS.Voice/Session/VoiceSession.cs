using System.Threading.Channels;
using Jarvis.Core.Commands;
using Jarvis.Core.Logging;
using Jarvis.Core.Text;
using Jarvis.Voice.Audio;
using Jarvis.Voice.Recognition;

namespace Jarvis.Voice.Session;

public enum VoiceState { Off, Idle, Listening, Recognizing, SoftDisabled, Error }

/// <summary>Параметры голосовой сессии (копируются из настроек).</summary>
public sealed class VoiceSessionOptions
{
    public List<string> WakePhrases { get; set; } = ["джарвис", "окей джарвис", "эй джарвис"];
    public string StopWord { get; set; } = "жёпа";
    public int CommandWindowSeconds { get; set; } = 6;
    public double VadThresholdDb { get; set; } = -42;
    public int EndSilenceMs { get; set; } = 800;
}

/// <summary>
/// Конвейер: микрофон → детектор речи (VAD) → распознавание фразы → разбор.
/// Слово-стоп обрабатывается с наивысшим приоритетом, в любом состоянии.
/// Звук держится только в памяти и не сохраняется.
/// </summary>
public sealed class VoiceSession : IDisposable
{
    private readonly IAudioSource _audio;
    private readonly ISpeechEngine _engine;
    private readonly IJarvisLog _log;
    private readonly Func<DateTime> _now;
    private readonly EnergyVad _vad = new();
    private readonly Channel<short[]> _queue = Channel.CreateBounded<short[]>(new BoundedChannelOptions(4)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private DateTime _windowUntil = DateTime.MinValue;
    private VoiceState _state = VoiceState.Off;
    private readonly Action<short[], int> _onSamples;
    private readonly Action<string> _onError;

    public VoiceSession(IAudioSource audio, ISpeechEngine engine, VoiceSessionOptions options, IJarvisLog? log = null, Func<DateTime>? now = null)
    {
        _audio = audio;
        _engine = engine;
        Options = options;
        _log = log ?? NullLog.Instance;
        _now = now ?? (() => DateTime.UtcNow);
        _onSamples = (buf, n) => { if (!Muted) _vad.Process(buf, n); };
        _onError = msg => { _log.Error($"Микрофон: {msg}"); SetState(VoiceState.Error); ErrorOccurred?.Invoke(msg); };
        _audio.SamplesAvailable += _onSamples;
        _audio.Error += _onError;
        _vad.Level += db => LevelChanged?.Invoke(db);
        _vad.SpeechStarted += () => SpeechStarted?.Invoke();
        _vad.UtteranceCompleted += samples => _queue.Writer.TryWrite(samples);
        ApplyOptions();
    }

    public VoiceSessionOptions Options { get; private set; }
    public VoiceState State => _state;
    /// <summary>Мягкое отключение («Джарвис, отключись»): слышим только «включись» и слово-стоп.</summary>
    public bool SoftDisabled { get; private set; }
    public bool Muted { get; set; }
    /// <summary>Ожидается ответ без фразы активации (подтверждение, «да/нет»).</summary>
    public bool ExpectingReply { get; set; }
    /// <summary>Пока JARVIS говорит, принимается только слово-стоп (защита от эха).</summary>
    public Func<bool>? IsSpeaking { get; set; }

    public event Action<VoiceState>? StateChanged;
    public event Action<double>? LevelChanged;
    public event Action? SpeechStarted;
    public event Action? WakeHeard;
    public event Action<string>? CommandHeard;
    public event Action<string>? StopWordHeard;
    public event Action? EnableRequested;
    public event Action<string>? Ignored;
    public event Action<string>? ErrorOccurred;

    public void UpdateOptions(VoiceSessionOptions options) { Options = options; ApplyOptions(); }

    private void ApplyOptions()
    {
        _vad.ThresholdDb = Options.VadThresholdDb;
        _vad.EndSilenceMs = Options.EndSilenceMs;
    }

    public void Start(int deviceNumber)
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerAsync(_cts.Token));
        _vad.Reset();
        _audio.Start(deviceNumber);
        SetState(SoftDisabled ? VoiceState.SoftDisabled : VoiceState.Idle);
    }

    public void Stop()
    {
        _audio.Stop();
        _cts?.Cancel();
        _cts = null;
        SetState(VoiceState.Off);
    }

    public void SetSoftDisabled(bool disabled)
    {
        SoftDisabled = disabled;
        _windowUntil = DateTime.MinValue;
        if (_state != VoiceState.Off) SetState(disabled ? VoiceState.SoftDisabled : VoiceState.Idle);
    }

    /// <summary>Открывает окно команды без фразы активации (например, по горячей клавише).</summary>
    public void OpenCommandWindow()
    {
        _windowUntil = _now().AddSeconds(Options.CommandWindowSeconds);
        SetState(VoiceState.Listening);
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var utt in _queue.Reader.ReadAllAsync(ct))
            {
                var prev = _state;
                SetState(VoiceState.Recognizing);
                RecognitionResult? r = null;
                try { r = _engine.Recognize(utt); }
                catch (Exception ex) { _log.Error($"Распознавание: {ex.Message}"); }
                SetState(prev == VoiceState.Recognizing ? IdleState() : prev);
                if (r is not null) HandleText(r.Text);
                else if (_state == VoiceState.Listening && _now() > _windowUntil) SetState(IdleState());
            }
        }
        catch (OperationCanceledException) { }
    }

    private VoiceState IdleState() => SoftDisabled ? VoiceState.SoftDisabled : VoiceState.Idle;

    /// <summary>Обработка распознанной фразы (публично для тестов и ручного ввода).</summary>
    public void HandleText(string text)
    {
        var n = TextNormalizer.Normalize(text);
        if (n.Length == 0) return;

        if (WakePhraseDetector.ContainsStopWord(n, Options.StopWord))
        {
            _windowUntil = DateTime.MinValue;
            SetState(IdleState());
            StopWordHeard?.Invoke(n);
            return;
        }
        if (IsSpeaking?.Invoke() == true) { Ignored?.Invoke(n); return; }

        var hasWake = WakePhraseDetector.TryStrip(n, Options.WakePhrases, out var rest);
        if (SoftDisabled)
        {
            if (hasWake && IsEnableCommand(rest)) { EnableRequested?.Invoke(); return; }
            Ignored?.Invoke(n);
            return;
        }
        if (hasWake)
        {
            if (rest.Length == 0)
            {
                OpenCommandWindow();
                WakeHeard?.Invoke();
                return;
            }
            Dispatch(rest);
            return;
        }
        if (ExpectingReply || _now() <= _windowUntil)
        {
            Dispatch(n);
            return;
        }
        Ignored?.Invoke(n);
    }

    private void Dispatch(string command)
    {
        _windowUntil = DateTime.MinValue;
        SetState(IdleState());
        CommandHeard?.Invoke(command);
    }

    internal static bool IsEnableCommand(string rest) =>
        rest is "включись" or "включайся" or "проснись" or "вернись" or "слушай";

    private void SetState(VoiceState s)
    {
        if (_state == s) return;
        _state = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>Останавливает сессию и освобождает движок. Источник звука не уничтожается (его можно переиспользовать).</summary>
    public void Dispose()
    {
        Stop();
        _audio.SamplesAvailable -= _onSamples;
        _audio.Error -= _onError;
        _engine.Dispose();
    }
}
