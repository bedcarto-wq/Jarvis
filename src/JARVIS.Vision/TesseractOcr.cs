using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Logging;
using Jarvis.Core.Vision;
using Jarvis.Platform;
using Tesseract;

namespace Jarvis.Vision;

/// <summary>
/// Распознавание текста на экране через Tesseract в классическом режиме
/// (EngineMode.TesseractOnly — без LSTM-нейросети). Требуются файлы *.traineddata
/// с классической моделью (репозиторий tesseract-ocr/tessdata).
/// </summary>
public sealed class TesseractOcr : IDisposable
{
    private readonly string _tessdata;
    private readonly string _languages;
    private readonly IJarvisLog _log;
    private TesseractEngine? _engine;
    private readonly object _sync = new();
    private bool _failed;

    private readonly Func<double> _scale;

    public TesseractOcr(string tessdataDir, string languages, IJarvisLog? log = null, Func<double>? scale = null)
    {
        _scale = scale ?? (() => 2.0);
        _tessdata = tessdataDir;
        _languages = string.IsNullOrWhiteSpace(languages) ? "rus+eng" : languages;
        _log = log ?? NullLog.Instance;
        Status = CheckFiles() ?? "Готов (загружается при первом использовании)";
    }

    public string Status { get; private set; }
    public bool Available => !_failed && CheckFiles() is null;

    private string? CheckFiles()
    {
        if (!Directory.Exists(_tessdata)) return $"Нет папки {_tessdata}";
        var missing = _languages.Split('+').Where(l => !File.Exists(Path.Combine(_tessdata, l + ".traineddata"))).ToList();
        return missing.Count == 0 ? null : $"Нет файлов языков: {string.Join(", ", missing.Select(m => m + ".traineddata"))} в {_tessdata}";
    }

    private TesseractEngine? Engine()
    {
        if (_engine is not null || _failed) return _engine;
        var problem = CheckFiles();
        if (problem is not null) { Status = problem; return null; }
        try
        {
            _engine = new TesseractEngine(_tessdata, _languages, EngineMode.TesseractOnly);
            _engine.DefaultPageSegMode = PageSegMode.SparseText;
            Status = $"Готов: {_languages}, классический режим";
        }
        catch (Exception ex)
        {
            _failed = true;
            Status = ex is DllNotFoundException or TypeInitializationException
                ? "Не загружены библиотеки Tesseract (нужен Microsoft Visual C++ Redistributable x64)"
                : $"OCR недоступен: {ex.Message}. Нужны классические (не LSTM-only) файлы traineddata.";
            _log.Warn(Status);
        }
        return _engine;
    }

    /// <summary>Распознаёт слова в прямоугольнике экрана. Координаты слов — экранные.</summary>
    public IReadOnlyList<OcrWord> Read(ScreenRect region, CancellationToken ct)
    {
        if (region.IsEmpty) return [];
        using var shot = ScreenCapture.Capture(new Rectangle(region.X, region.Y, region.Width, region.Height));
        return Recognize(shot, region.X, region.Y, Math.Clamp(_scale(), 1.0, 3.0), ct);
    }

    /// <summary>Распознаёт готовое изображение (используется и бенчмарком на тестовых картинках).</summary>
    public IReadOnlyList<OcrWord> Recognize(Bitmap image, int offsetX, int offsetY, double scale, CancellationToken ct)
    {
        lock (_sync)
        {
            var engine = Engine();
            if (engine is null) return [];
            scale = Math.Clamp(scale, 1.0, 3.0);
            Bitmap? scaled = null;
            try
            {
                var src = image;
                if (scale > 1.001)
                {
                    scaled = new Bitmap((int)(image.Width * scale), (int)(image.Height * scale), PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(image, 0, 0, scaled.Width, scaled.Height);
                    }
                    src = scaled;
                }
                using var ms = new MemoryStream();
                src.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ct.ThrowIfCancellationRequested();
                using var pix = Pix.LoadFromMemory(ms.ToArray());
                using var page = engine.Process(pix);
                var words = new List<OcrWord>();
                using var it = page.GetIterator();
                it.Begin();
                do
                {
                    var text = it.GetText(PageIteratorLevel.Word)?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    if (!it.TryGetBoundingBox(PageIteratorLevel.Word, out var b)) continue;
                    words.Add(new OcrWord(text, new ScreenRect(offsetX + (int)(b.X1 / scale), offsetY + (int)(b.Y1 / scale), (int)(b.Width / scale), (int)(b.Height / scale)),
                        it.GetConfidence(PageIteratorLevel.Word) / 100f));
                } while (it.Next(PageIteratorLevel.Word));
                return words;
            }
            finally { scaled?.Dispose(); }
        }
    }

    /// <summary>Загружает движок заранее (для замера времени инициализации).</summary>
    public bool Warmup() { lock (_sync) return Engine() is not null; }

    public void Dispose() { lock (_sync) { _engine?.Dispose(); _engine = null; } }
}
