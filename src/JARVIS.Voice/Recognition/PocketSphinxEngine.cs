using System.Runtime.InteropServices;
using Jarvis.Core.Logging;

namespace Jarvis.Voice.Recognition;

/// <summary>
/// Офлайн-распознавание по ограниченной грамматике (JSGF) на CMU PocketSphinx.
/// Модель: cmusphinx-ru-5.2 (GMM-HMM). Нейросети не используются.
/// </summary>
public sealed class PocketSphinxEngine : ISpeechEngine
{
    private readonly string _modelDir;
    private readonly string _workDir;
    private readonly IJarvisLog _log;
    private readonly object _sync = new();
    private PronunciationDictionary? _pron;
    private readonly HashSet<string> _knownWords = new(StringComparer.Ordinal);
    private IntPtr _ps;
    private int _grammarCounter;
    private GrammarSpec? _grammar;

    public PocketSphinxEngine(string modelDir, string workDir, IJarvisLog? log = null)
    {
        _modelDir = modelDir;
        _workDir = workDir;
        _log = log ?? NullLog.Instance;
    }

    public string Name => "PocketSphinx (CMU Sphinx, ru 5.2)";
    public bool IsReady => _ps != IntPtr.Zero;
    public string Status { get; private set; } = "Не инициализирован";

    /// <summary>Строгость 0..1: чем выше, тем больше отбрасывается неуверенных гипотез.</summary>
    public double Strictness { get; set; } = 0.5;

    /// <summary>Параметры профиля производительности: maxhmmpf и ширина луча beam = 1e-N.</summary>
    public int MaxHmmPf { get; set; } = 3000;
    public int BeamExp { get; set; } = 30;

    /// <summary>Проверяет, что нативная библиотека загружается и экспортирует API (без модели).</summary>
    public static void ProbeNativeLibrary()
    {
        PocketSphinxNative.EnsureResolver();
        var cfg = PocketSphinxNative.ps_config_init(IntPtr.Zero);
        if (cfg == IntPtr.Zero) throw new InvalidOperationException("ps_config_init вернул NULL");
        PocketSphinxNative.ps_config_free(cfg);
    }

    public static string? ValidateModel(string modelDir)
    {
        if (!Directory.Exists(modelDir)) return $"Папка модели не найдена: {modelDir}";
        foreach (var f in new[] { "mdef", "means", "variances", "mixture_weights", "feat.params" })
            if (!File.Exists(Path.Combine(modelDir, f))) return $"В модели нет файла {f}";
        if (!File.Exists(Path.Combine(modelDir, "ru.dic"))) return "В модели нет словаря ru.dic (будут использованы только правила произношения)";
        return null;
    }

    public void Initialize(GrammarSpec grammar)
    {
        lock (_sync)
        {
            DisposeDecoder();
            var problem = ValidateModel(_modelDir);
            if (problem is not null && !problem.Contains("ru.dic"))
            {
                Status = problem;
                throw new InvalidOperationException(problem);
            }
            PocketSphinxNative.EnsureResolver();
            _pron = new PronunciationDictionary(Path.Combine(_modelDir, "ru.dic"));
            var entries = _pron.Resolve(grammar.AllWords());
            var dicPath = Path.Combine(_workDir, "jarvis.dic");
            PronunciationDictionary.WriteDic(dicPath, entries);
            _knownWords.Clear();
            foreach (var e in entries) _knownWords.Add(e.Word);

            try { PocketSphinxNative.err_set_loglevel_str("ERROR"); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                Status = "Не найдена библиотека pocketsphinx.dll рядом с программой";
                throw new InvalidOperationException(Status, ex);
            }
            var config = PocketSphinxNative.ps_config_init(IntPtr.Zero);
            if (config == IntPtr.Zero) throw new InvalidOperationException("ps_config_init вернул NULL");
            PocketSphinxNative.ps_config_set_str(config, "hmm", _modelDir);
            PocketSphinxNative.ps_config_set_str(config, "dict", dicPath);
            PocketSphinxNative.ps_config_set_str(config, "loglevel", "ERROR");
            PocketSphinxNative.ps_config_set_str(config, "samprate", "16000");
            PocketSphinxNative.ps_config_set_str(config, "bestpath", "no");
            var beam = Math.Clamp(BeamExp, 15, 60);
            PocketSphinxNative.ps_config_set_str(config, "beam", $"1e-{beam}");
            PocketSphinxNative.ps_config_set_str(config, "wbeam", $"1e-{Math.Max(5, beam - 10)}");
            PocketSphinxNative.ps_config_set_str(config, "pbeam", $"1e-{beam}");
            PocketSphinxNative.ps_config_set_str(config, "maxhmmpf", Math.Clamp(MaxHmmPf, 500, 10000).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ps = PocketSphinxNative.ps_init(config);
            if (_ps == IntPtr.Zero)
            {
                PocketSphinxNative.ps_config_free(config);
                Status = "Не удалось загрузить акустическую модель";
                throw new InvalidOperationException(Status);
            }
            // config принадлежит декодеру после ps_init (счётчик ссылок); освобождаем свою ссылку.
            PocketSphinxNative.ps_config_free(config);
            ApplyGrammar(grammar);
            Status = _pron.UnknownWords.Count == 0
                ? $"Готов: {_knownWords.Count} слов в словаре"
                : $"Готов; без произношения: {string.Join(", ", _pron.UnknownWords.Take(5))}";
            _log.Info($"PocketSphinx: {Status}");
        }
    }

    public void UpdateGrammar(GrammarSpec grammar)
    {
        lock (_sync)
        {
            if (_ps == IntPtr.Zero) { Initialize(grammar); return; }
            ApplyGrammar(grammar);
        }
    }

    private void ApplyGrammar(GrammarSpec grammar)
    {
        var newWords = grammar.AllWords().Where(w => !_knownWords.Contains(w)).ToList();
        if (newWords.Count > 0 && _pron is not null)
        {
            var entries = _pron.Resolve(newWords);
            for (var i = 0; i < entries.Count; i++)
            {
                var (w, p) = entries[i];
                var update = i == entries.Count - 1 ? 1 : 0;
                if (PocketSphinxNative.ps_add_word(_ps, w, p, update) >= 0) _knownWords.Add(w);
                else _log.Warn($"PocketSphinx: не удалось добавить слово «{w}»");
            }
        }
        // Слова без произношения исключаем, иначе грамматика не скомпилируется.
        var effective = FilterUnknown(grammar);
        var name = $"g{++_grammarCounter}";
        if (PocketSphinxNative.ps_add_jsgf_string(_ps, name, effective.ToJsgf()) != 0)
            throw new InvalidOperationException("Не удалось скомпилировать грамматику JSGF");
        if (PocketSphinxNative.ps_activate_search(_ps, name) != 0)
            throw new InvalidOperationException("Не удалось активировать грамматику");
        _grammar = effective;
    }

    private GrammarSpec FilterUnknown(GrammarSpec g)
    {
        bool Ok(string phrase) => GrammarSpec.Clean(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(_knownWords.Contains);
        return new GrammarSpec
        {
            WakePhrases = g.WakePhrases.Where(Ok).ToList(),
            StopWord = Ok(g.StopWord) ? g.StopWord : "",
            AppNames = g.AppNames.Where(Ok).ToList(),
            SiteNames = g.SiteNames.Where(Ok).ToList(),
            TemplatePhrases = g.TemplatePhrases.Where(Ok).ToList(),
            Names = g.Names.Where(Ok).ToList(),
            IncludeGarbage = g.IncludeGarbage,
        };
    }

    public unsafe RecognitionResult? Recognize(short[] samples)
    {
        lock (_sync)
        {
            if (_ps == IntPtr.Zero || samples.Length == 0) return null;
            if (PocketSphinxNative.ps_start_utt(_ps) < 0) return null;
            fixed (short* p = samples)
                PocketSphinxNative.ps_process_raw(_ps, p, (nuint)samples.Length, 0, 0);
            PocketSphinxNative.ps_end_utt(_ps);
            var hypPtr = PocketSphinxNative.ps_get_hyp(_ps, out var score);
            var hyp = hypPtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(hypPtr);
            if (string.IsNullOrWhiteSpace(hyp)) return null;
            var frames = Math.Max(1, PocketSphinxNative.ps_get_n_frames(_ps));
            // Апостериорная вероятность (ps_get_prob) требует построения решётки и замедляет
            // распознавание в несколько раз, поэтому оценка уверенности — по акустическому счёту на кадр.
            var confidence = ScoreToConfidence(score / (double)frames);
            var stripped = _grammar is null ? hyp : GrammarSpec.StripGarbage(hyp, _grammar);
            _log.Debug($"PocketSphinx: «{hyp}» → «{stripped}», score/frame={score / (double)frames:F0}, p={confidence:F2}");
            if (stripped.Length == 0) return null;
            if (confidence < MinConfidence(Strictness)) { _log.Debug("PocketSphinx: отброшено по уверенности"); return null; }
            return new RecognitionResult(stripped, confidence);
        }
    }

    /// <summary>ps_get_prob возвращает логарифм апостериорной вероятности по основанию 1.0001.</summary>
    internal static double ToConfidence(int logProb) => logProb >= 0 ? 1.0 : Math.Exp(logProb * Math.Log(1.0001));

    /// <summary>Грубая эвристика: средний лог-счёт на кадр → 0..1 (типичные значения −2000…−9000).</summary>
    internal static double ScoreToConfidence(double perFrame) => Math.Clamp(1.0 - (-perFrame - 2000) / 9000.0, 0, 1);

    internal static double MinConfidence(double strictness) => Math.Clamp(strictness, 0, 1) switch
    {
        <= 0.1 => 0,
        var s => 0.02 + 0.5 * (s - 0.1),
    };

    private void DisposeDecoder()
    {
        if (_ps != IntPtr.Zero) { PocketSphinxNative.ps_free(_ps); _ps = IntPtr.Zero; }
    }

    public void Dispose()
    {
        lock (_sync) DisposeDecoder();
    }
}
