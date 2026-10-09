using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using Jarvis.Core.Performance;
using Jarvis.Core.Text;

namespace Jarvis.Vision;

/// <summary>
/// OCR на заранее подготовленных изображениях с известным текстом (рисуются в памяти —
/// никаких снимков экрана пользователя). Классический движок Tesseract (EngineMode.TesseractOnly).
/// </summary>
public sealed class OcrBenchmarkProbe(string tessdataDir, string languages) : IBenchmarkProbe
{
    public const string ExpectedText = "Открыть файл Сохранить Настройки Отправить сообщение Settings Cancel 2024";

    public string Title => "OCR (Tesseract, классический движок)";
    public BenchmarkStage Stage => BenchmarkStage.Measuring;

    public Task<IReadOnlyList<Measurement>> RunAsync(BenchmarkContext ctx, CancellationToken ct)
    {
        const string g = "OCR";
        var list = new List<Measurement>();
        using var ocr = new TesseractOcr(tessdataDir, languages);
        using var proc = Process.GetCurrentProcess();
        var mem0 = proc.PrivateMemorySize64;
        var sw = Stopwatch.StartNew();
        if (!ocr.Warmup())
        {
            list.Add(Measurement.Unavailable(MKeys.OcrInitMs, g, "Загрузка OCR", ocr.Status));
            list.Add(Measurement.Unavailable(MKeys.OcrMsScale2, g, "Распознавание тестового окна", "OCR недоступен"));
            return Task.FromResult<IReadOnlyList<Measurement>>(list);
        }
        list.Add(new(MKeys.OcrInitMs, g, "Загрузка OCR", sw.Elapsed.TotalMilliseconds, "мс", null, ocr.Status));

        using var img = RenderTestImage(1280, 720);
        string recognized = "";
        foreach (var (scale, key) in new[] { (1.0, MKeys.OcrMsScale1), (1.5, MKeys.OcrMsScale15), (2.0, MKeys.OcrMsScale2) })
        {
            ct.ThrowIfCancellationRequested();
            sw.Restart();
            var words = ocr.Recognize(img, 0, 0, scale, ct);
            list.Add(new(key, g, $"Тестовое окно 1280×720, увеличение {scale:0.#}×", sw.Elapsed.TotalMilliseconds, "мс", null, $"слов: {words.Count}"));
            if (scale == 2.0) recognized = string.Join(" ", words.Select(w => w.Text));
        }
        var acc = Fuzzy.Similarity(TextNormalizer.Normalize(recognized), TextNormalizer.Normalize(ExpectedText)) * 100;
        list.Add(new(MKeys.OcrAccuracy, g, "Совпадение с известным текстом (2×)", Math.Round(acc, 1), "%", null, $"Распознано: «{recognized}»"));
        proc.Refresh();
        if (mem0 > 0) list.Add(new(MKeys.OcrMemMb, g, "Дополнительная память OCR", Math.Max(0, (proc.PrivateMemorySize64 - mem0) / 1048576.0), "МБ"));
        return Task.FromResult<IReadOnlyList<Measurement>>(list);
    }

    /// <summary>Изображение «окна программы» с известным текстом разного размера.</summary>
    public static Bitmap RenderTestImage(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var bar = new SolidBrush(Color.FromArgb(240, 240, 240));
        g.FillRectangle(bar, 0, 0, w, 40);
        var words = ExpectedText.Split(' ');
        using var f1 = new Font("Segoe UI", 14);
        using var f2 = new Font("Segoe UI", 11);
        using var f3 = new Font("Segoe UI", 20, FontStyle.Bold);
        g.DrawString($"{words[0]} {words[1]}", f1, Brushes.Black, 12, 8);
        g.DrawString(words[2], f1, Brushes.Black, 220, 8);
        g.DrawString(words[3], f1, Brushes.Black, 380, 8);
        g.DrawString($"{words[4]} {words[5]}", f3, Brushes.Black, 60, 200);
        g.DrawString($"{words[6]} {words[7]}", f2, Brushes.Black, 60, 400);
        g.DrawString(words[8], f2, Brushes.DimGray, w - 120, h - 40);
        return bmp;
    }
}
