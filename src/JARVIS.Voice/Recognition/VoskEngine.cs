using System.Text.Json;
using Jarvis.Core.Logging;
using Jarvis.Core.Text;
using Vosk;

namespace Jarvis.Voice.Recognition;

/// <summary>
/// Офлайн-распознавание на Vosk (Kaldi) с лёгкой нейросетевой моделью vosk-model-small-ru (~45 МБ).
/// Работает в режиме ограниченного словаря: распознаватель выбирает только из фраз активации,
/// слова-стоп и команд, а всё постороннее уходит в «[unk]» и отбрасывается.
/// </summary>
public sealed class VoskEngine : ISpeechEngine
{
    public const string DefaultModelName = "vosk-model-small-ru-0.22";
    private const int SampleRate = 16000;
    /// <summary>Vosk ждёт кириллицу как есть (UTF-8), а не в виде \uXXXX — иначе все слова «вне словаря».</summary>
    private static readonly JsonSerializerOptions GrammarJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _modelDir;
    private readonly IJarvisLog _log;
    private readonly object _sync = new();
    private Model? _model;
    private VoskRecognizer? _rec;
    /// <summary>Вариант слова для модели → исходное слово (например, «жопа» → «жёпа»).</summary>
    private Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    public VoskEngine(string modelDir, IJarvisLog? log = null)
    {
        _modelDir = modelDir;
        _log = log ?? NullLog.Instance;
    }

    public string Name => "Vosk (small-ru, офлайн)";
    public bool IsReady => _rec is not null;
    public string Status { get; private set; } = "Не инициализирован";

    /// <summary>Строгость 0..1: минимальная средняя уверенность слов фразы.</summary>
    public double Strictness { get; set; } = 0.5;

    /// <summary>Использовать ограниченный словарь команд (рекомендуется). Иначе — свободное распознавание.</summary>
    public bool UseGrammar { get; set; } = true;

    public static string? ValidateModel(string modelDir)
    {
        if (!Directory.Exists(modelDir)) return $"Папка модели Vosk не найдена: {modelDir}";
        if (!File.Exists(Path.Combine(modelDir, "am", "final.mdl"))) return "В папке нет am\\final.mdl — это не модель Vosk";
        if (!File.Exists(Path.Combine(modelDir, "conf", "model.conf"))) return "В модели нет conf\\model.conf";
        if (!Directory.Exists(Path.Combine(modelDir, "graph"))) return "В модели нет папки graph";
        return null;
    }

    public void Initialize(GrammarSpec grammar)
    {
        lock (_sync)
        {
            DisposeAll();
            var problem = ValidateModel(_modelDir);
            if (problem is not null) { Status = problem; throw new InvalidOperationException(problem); }
            try
            {
                Vosk.Vosk.SetLogLevel(-1);
                _model = new Model(_modelDir);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                Status = "Не найдена библиотека libvosk.dll рядом с программой";
                throw new InvalidOperationException(Status, ex);
            }
            ApplyGrammar(grammar);
            _log.Info($"Vosk: {Status}");
        }
    }

    public void UpdateGrammar(GrammarSpec grammar)
    {
        lock (_sync)
        {
            if (_model is null) { Initialize(grammar); return; }
            ApplyGrammar(grammar);
        }
    }

    private void ApplyGrammar(GrammarSpec grammar)
    {
        var (phrases, aliases) = BuildPhrases(grammar);
        _aliases = aliases;
        _rec?.Dispose();
        _rec = UseGrammar
            ? new VoskRecognizer(_model!, SampleRate, JsonSerializer.Serialize(phrases, GrammarJson))
            : new VoskRecognizer(_model!, SampleRate);
        _rec.SetWords(true);
        Status = UseGrammar ? $"Готов: {phrases.Count - 1} фраз в словаре команд" : "Готов: свободное распознавание";
    }

    /// <summary>
    /// Фразы для ограниченного словаря. Vosk допускает последовательности фраз,
    /// поэтому «джарвис» + «открой браузер» распознаётся и без явного объединения.
    /// В словаре модели нет буквы «ё», поэтому для таких слов добавляются варианты с «е» и «о».
    /// </summary>
    internal static (List<string> Phrases, Dictionary<string, string> Aliases) BuildPhrases(GrammarSpec g)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string phrase)
        {
            var clean = GrammarSpec.Clean(phrase);
            if (clean.Length == 0) return;
            foreach (var v in Variants(clean, aliases))
                if (seen.Add(v)) result.Add(v);
        }
        foreach (var w in g.WakePhrases) Add(w);
        if (g.StopWord.Length > 0) Add(g.StopWord);
        foreach (var c in g.ExpandCommands()) Add(c);
        result.Add("[unk]");
        return (result, aliases);
    }

    private static IEnumerable<string> Variants(string phrase, Dictionary<string, string> aliases)
    {
        if (!phrase.Contains('ё')) { yield return phrase; yield break; }
        foreach (var repl in new[] { 'е', 'о' })
        {
            var v = phrase.Replace('ё', repl);
            foreach (var (a, b) in v.Split(' ').Zip(phrase.Split(' ')))
                if (a != b) aliases.TryAdd(a, b);
            yield return v;
        }
    }

    public RecognitionResult? Recognize(short[] samples)
    {
        lock (_sync)
        {
            if (_rec is null || samples.Length == 0) return null;
            _rec.AcceptWaveform(samples, samples.Length);
            var json = _rec.FinalResult();
            var (text, confidence) = Parse(json, _aliases);
            _log.Debug($"Vosk: «{text}», p={confidence:F2}");
            if (text.Length == 0) return null;
            if (confidence < MinConfidence(Strictness)) { _log.Debug("Vosk: отброшено по уверенности"); return null; }
            return new RecognitionResult(text, confidence);
        }
    }

    /// <summary>Разбирает ответ Vosk: текст без «[unk]» и средняя уверенность распознанных слов.</summary>
    internal static (string Text, double Confidence) Parse(string json, IReadOnlyDictionary<string, string>? aliases = null)
    {
        using var doc = JsonDocument.Parse(json);
        var words = new List<string>();
        var confs = new List<double>();
        if (doc.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in arr.EnumerateArray())
            {
                var word = w.TryGetProperty("word", out var x) ? x.GetString() ?? "" : "";
                if (word.Length == 0 || word == "[unk]") continue;
                words.Add(aliases is not null && aliases.TryGetValue(word, out var orig) ? orig : word);
                confs.Add(w.TryGetProperty("conf", out var c) ? c.GetDouble() : 1.0);
            }
        }
        else if (doc.RootElement.TryGetProperty("text", out var t))
        {
            foreach (var word in (t.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (word != "[unk]") { words.Add(aliases is not null && aliases.TryGetValue(word, out var orig) ? orig : word); confs.Add(1.0); }
        }
        return (string.Join(' ', words), confs.Count == 0 ? 0 : confs.Average());
    }

    /// <summary>Строгость 0 → принимать всё; 0,5 → средняя уверенность от 0,6; 1 → от 0,9.</summary>
    internal static double MinConfidence(double strictness) => Math.Clamp(strictness, 0, 1) switch
    {
        <= 0.05 => 0,
        var s => 0.3 + 0.6 * s,
    };

    private void DisposeAll()
    {
        _rec?.Dispose(); _rec = null;
        _model?.Dispose(); _model = null;
    }

    public void Dispose()
    {
        lock (_sync) DisposeAll();
    }
}
