using Jarvis.Core.Logging;
using Jarvis.Core.Text;
using SherpaOnnx;

namespace Jarvis.Voice.Recognition;

/// <summary>
/// Офлайн-распознавание русской речи моделью GigaAM v3 (Сбер, MIT) через sherpa-onnx.
/// Модель CTC, int8, ~225 МБ, работает на процессоре. Распознаёт свободную речь целиком
/// (фраза приходит после детектора речи), а затем текст приводится к словарю команд:
/// слова, близкие по написанию к известным («джарвиз» → «джарвис»), заменяются на точные.
/// </summary>
public sealed class GigaAmEngine : ISpeechEngine
{
    public const string DefaultModelName = "gigaam-v3-ctc";
    private const int SampleRate = 16000;

    private readonly string _modelDir;
    private readonly IJarvisLog _log;
    private readonly object _sync = new();
    private OfflineRecognizer? _rec;
    private HashSet<string> _vocabulary = new(StringComparer.Ordinal);

    public GigaAmEngine(string modelDir, IJarvisLog? log = null)
    {
        _modelDir = modelDir;
        _log = log ?? NullLog.Instance;
    }

    public string Name => "GigaAM v3 (Сбер, офлайн)";
    public bool IsReady => _rec is not null;
    public string Status { get; private set; } = "Не инициализирован";
    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Строгость 0..1: насколько близким должно быть слово к словарю, чтобы его исправить.</summary>
    public double Strictness { get; set; } = 0.5;

    public static string ModelFile(string dir) => Path.Combine(dir, "model.int8.onnx");
    public static string TokensFile(string dir) => Path.Combine(dir, "tokens.txt");

    public static string? ValidateModel(string modelDir)
    {
        if (!Directory.Exists(modelDir)) return $"Папка модели GigaAM не найдена: {modelDir}";
        if (!File.Exists(ModelFile(modelDir))) return "В папке нет model.int8.onnx";
        if (!File.Exists(TokensFile(modelDir))) return "В папке нет tokens.txt";
        return null;
    }

    public void Initialize(GrammarSpec grammar)
    {
        lock (_sync)
        {
            Dispose(false);
            var problem = ValidateModel(_modelDir);
            if (problem is not null) { Status = problem; throw new InvalidOperationException(problem); }
            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = SampleRate;
            config.FeatConfig.FeatureDim = 64;
            config.ModelConfig.NeMoCtc.Model = NativePath.Safe(ModelFile(_modelDir));
            config.ModelConfig.Tokens = NativePath.Safe(TokensFile(_modelDir));
            config.ModelConfig.NumThreads = Threads;
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.Debug = 0;
            config.DecodingMethod = "greedy_search";
            try { _rec = new OfflineRecognizer(config); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                Status = "Не найдена библиотека sherpa-onnx рядом с программой";
                throw new InvalidOperationException(Status, ex);
            }
            ApplyGrammar(grammar);
            _log.Info($"GigaAM: {Status}");
        }
    }

    public void UpdateGrammar(GrammarSpec grammar)
    {
        lock (_sync)
        {
            if (_rec is null) { Initialize(grammar); return; }
            ApplyGrammar(grammar);
        }
    }

    private void ApplyGrammar(GrammarSpec grammar)
    {
        _vocabulary = grammar.AllWords().Except(GrammarSpec.GarbageWords)
            .Select(TextNormalizer.Normalize).Where(w => w.Length > 0).ToHashSet(StringComparer.Ordinal);
        Status = $"Готов: свободная речь, {_vocabulary.Count} слов команд для исправления, потоков: {Threads}";
    }

    public RecognitionResult? Recognize(short[] samples)
    {
        lock (_sync)
        {
            if (_rec is null || samples.Length == 0) return null;
            var f = new float[samples.Length];
            for (var i = 0; i < samples.Length; i++) f[i] = samples[i] / 32768f;
            using var stream = _rec.CreateStream();
            stream.AcceptWaveform(SampleRate, f);
            _rec.Decode(stream);
            var raw = stream.Result.Text ?? "";
            var text = SnapToVocabulary(TextNormalizer.Normalize(raw), _vocabulary, Strictness);
            _log.Debug($"GigaAM: «{raw}» → «{text}»");
            return text.Length == 0 ? null : new RecognitionResult(text, 1.0);
        }
    }

    /// <summary>
    /// Заменяет слова, похожие на слова команд, на точное написание. Порог — доля
    /// расстояния Левенштейна от длины слова: при строгости 0,5 допускается ~1 ошибка на 4 буквы.
    /// </summary>
    internal static string SnapToVocabulary(string normalized, IReadOnlyCollection<string> vocabulary, double strictness)
    {
        if (normalized.Length == 0 || vocabulary.Count == 0) return normalized;
        var maxRatio = 0.4 - 0.3 * Math.Clamp(strictness, 0, 1);
        var words = TextNormalizer.Words(normalized);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length < 4 || vocabulary.Contains(w)) continue;
            string? best = null;
            var bestD = int.MaxValue;
            foreach (var v in vocabulary)
            {
                if (Math.Abs(v.Length - w.Length) > 2) continue;
                var d = Levenshtein(w, v, bestD);
                if (d < bestD) { bestD = d; best = v; }
            }
            if (best is not null && bestD <= Math.Max(1, (int)Math.Floor(w.Length * maxRatio))) words[i] = best;
        }
        return string.Join(' ', words);
    }

    internal static int Levenshtein(string a, string b, int cap = int.MaxValue)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin >= cap) return cap;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private void Dispose(bool _)
    {
        _rec?.Dispose();
        _rec = null;
    }

    public void Dispose()
    {
        lock (_sync) Dispose(true);
    }
}
