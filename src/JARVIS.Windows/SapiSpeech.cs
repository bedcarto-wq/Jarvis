using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Logging;
using Jarvis.Voice.Recognition;
using RecognitionResult = Jarvis.Voice.Recognition.RecognitionResult;

namespace Jarvis.Platform;

/// <summary>Короткие голосовые ответы через встроенный синтезатор Windows (SAPI, офлайн).</summary>
public sealed class SapiTts : ISpeechOutput, IDisposable
{
    private readonly SpeechSynthesizer? _synth;
    private volatile bool _speaking;

    public SapiTts(string? voiceName, int rate, int volume, IJarvisLog? log = null)
    {
        try
        {
            _synth = new SpeechSynthesizer();
            _synth.SetOutputToDefaultAudioDevice();
            _synth.SpeakStarted += (_, _) => _speaking = true;
            _synth.SpeakCompleted += (_, _) => _speaking = false;
            Configure(voiceName, rate, volume);
        }
        catch (Exception ex)
        {
            _synth = null;
            StatusMessage = $"Синтез речи недоступен: {ex.Message}";
            log?.Warn(StatusMessage);
        }
    }

    public bool IsAvailable => _synth is not null;
    public bool IsSpeaking => _speaking;
    public string StatusMessage { get; private set; } = "";
    public string? CurrentVoice { get; private set; }

    public IReadOnlyList<string> GetVoices() =>
        _synth?.GetInstalledVoices().Where(v => v.Enabled).Select(v => $"{v.VoiceInfo.Name} ({v.VoiceInfo.Culture.Name})").ToList() ?? [];

    public void Configure(string? voiceName, int rate, int volume)
    {
        if (_synth is null) return;
        _synth.Rate = Math.Clamp(rate, -10, 10);
        _synth.Volume = Math.Clamp(volume, 0, 100);
        var voices = _synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
        var chosen = voices.FirstOrDefault(v => voiceName is not null && voiceName.StartsWith(v.Name, StringComparison.OrdinalIgnoreCase))
                     ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == "ru");
        if (chosen is not null)
        {
            _synth.SelectVoice(chosen.Name);
            CurrentVoice = chosen.Name;
            StatusMessage = chosen.Culture.TwoLetterISOLanguageName == "ru"
                ? $"Голос: {chosen.Name}"
                : $"Голос: {chosen.Name}. Русский голос не найден — установите языковой пакет «Русский» с функцией «Речь».";
        }
        else
        {
            CurrentVoice = voices.FirstOrDefault()?.Name;
            StatusMessage = "Русский голос не установлен. Ответы будут показываться текстом в HUD. " +
                            "Установите: Параметры → Время и язык → Язык → Русский → Речь.";
        }
    }

    public bool HasRussianVoice => _synth?.GetInstalledVoices().Any(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "ru") == true;

    public void Say(string text)
    {
        if (_synth is null || string.IsNullOrWhiteSpace(text)) return;
        if (!HasRussianVoice && text.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я')) return; // англ. голос русский текст не прочтёт
        _synth.SpeakAsyncCancelAll();
        _speaking = true;
        _synth.SpeakAsync(text);
    }

    public void Cancel()
    {
        try { _synth?.SpeakAsyncCancelAll(); } catch { }
        _speaking = false;
    }

    public void Dispose() => _synth?.Dispose();
}

/// <summary>
/// Альтернативный распознаватель: Windows SAPI (System.Speech) с ограниченной грамматикой.
/// Требует установленного русского распознавателя речи Windows (есть не во всех редакциях).
/// </summary>
public sealed class SapiRecognizer : ISpeechEngine
{
    private readonly string _culture;
    private readonly IJarvisLog _log;
    private SpeechRecognitionEngine? _engine;
    private readonly object _sync = new();

    public SapiRecognizer(string culture, IJarvisLog? log = null) { _culture = culture; _log = log ?? NullLog.Instance; }

    public string Name => "Windows SAPI";
    public bool IsReady => _engine is not null;
    public string Status { get; private set; } = "Не инициализирован";
    public double MinConfidence { get; set; } = 0.5;

    public static IReadOnlyList<string> InstalledCultures()
    {
        try { return SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture.Name).ToList(); }
        catch { return []; }
    }

    public void Initialize(GrammarSpec grammar)
    {
        lock (_sync)
        {
            _engine?.Dispose();
            _engine = null;
            var info = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => r.Culture.Name.Equals(_culture, StringComparison.OrdinalIgnoreCase));
            if (info is null)
            {
                Status = $"Распознаватель Windows для {_culture} не установлен (есть: {string.Join(", ", InstalledCultures())})";
                throw new InvalidOperationException(Status);
            }
            _engine = new SpeechRecognitionEngine(info);
            LoadGrammar(grammar);
            Status = $"Готов: {info.Description}";
        }
    }

    public void UpdateGrammar(GrammarSpec grammar)
    {
        lock (_sync)
        {
            if (_engine is null) { Initialize(grammar); return; }
            LoadGrammar(grammar);
        }
    }

    private void LoadGrammar(GrammarSpec grammar)
    {
        _engine!.UnloadAllGrammars();
        var phrases = grammar.ExpandPhrases(20000);
        var choices = new Choices(phrases.ToArray());
        var gb = new GrammarBuilder(choices) { Culture = new CultureInfo(_culture) };
        _engine.LoadGrammar(new Grammar(gb) { Name = "jarvis" });
    }

    public RecognitionResult? Recognize(short[] samples)
    {
        lock (_sync)
        {
            if (_engine is null) return null;
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            using var ms = new MemoryStream(bytes);
            _engine.SetInputToAudioStream(ms, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            var r = _engine.Recognize(TimeSpan.FromSeconds(15));
            _engine.SetInputToNull();
            if (r is null) return null;
            _log.Debug($"SAPI: «{r.Text}» {r.Confidence:F2}");
            return r.Confidence < MinConfidence ? null : new RecognitionResult(r.Text, r.Confidence);
        }
    }

    public void Dispose()
    {
        lock (_sync) { _engine?.Dispose(); _engine = null; }
    }
}
