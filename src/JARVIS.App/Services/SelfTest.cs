using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jarvis.Core;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Apps;
using Jarvis.Core.Settings;
using Jarvis.Platform;
using Jarvis.Vision;
using Jarvis.Voice.Recognition;

namespace Jarvis.App.Services;

/// <summary>
/// Неинтерактивная самопроверка собранного приложения на реальной Windows:
/// JARVIS.exe --selftest [--report путь] [--model папка] [--vosk-model папка] [--tessdata папка] [--desktop] [--benchmark]
/// Проверяет загрузку нативных библиотек, распознаватель, OCR, SAPI, окна и
/// (с --desktop) сквозной сценарий «открой/сверни/закрой блокнот» через конвейер команд.
/// </summary>
internal static class SelfTest
{
    private static readonly StringBuilder Report = new();
    private static int _failed, _ok, _skipped;

    public static async Task<int> RunAsync(string[] args)
    {
        string Arg(string name, string def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        var reportPath = Arg("--report", Path.Combine(AppContext.BaseDirectory, "selftest-report.txt"));
        var model = Arg("--model", "");
        var voskModel = Arg("--vosk-model", "");
        var tess = Arg("--tessdata", "");
        var desktop = args.Contains("--desktop");

        Line($"JARVIS self-test {typeof(SelfTest).Assembly.GetName().Version}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"ОС: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}), x64={Environment.Is64BitProcess}, .NET {Environment.Version}");

        var root = Path.Combine(Path.GetTempPath(), "JARVIS-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        JarvisController? c = null;
        Check("Composition root (настройки, каталог, шаблоны, сервисы)", () =>
        {
            c = new JarvisController(new AppPaths(root));
            return $"программ в каталоге: {c.Catalog.All.Count}, шаблонов: {c.Templates.All.Count}";
        });

        Check("pocketsphinx.dll загружается и экспортирует API", () =>
        {
            PocketSphinxEngine.ProbeNativeLibrary();
            return "ps_config_init/ps_config_free OK";
        });

        if (Directory.Exists(model))
            Check("PocketSphinx: инициализация декодера с грамматикой команд", () =>
            {
                using var engine = new PocketSphinxEngine(model, Path.Combine(root, "models"));
                var spec = VocabularyBuilder.Build(new JarvisSettings(), BuiltInApps.All.Select(BuiltInApps.ToEntry), ["рабочий режим"]);
                var sw = Stopwatch.StartNew();
                engine.Initialize(spec);
                var init = sw.ElapsedMilliseconds;
                if (!engine.IsReady) throw new InvalidOperationException(engine.Status);
                sw.Restart();
                var silent = engine.Recognize(new short[32000]);
                var rnd = new Random(7);
                engine.Recognize(Enumerable.Range(0, 32000).Select(_ => (short)rnd.Next(-400, 400)).ToArray());
                return $"слов: {spec.AllWords().Count()}, init {init} мс, 2×2 с аудио {sw.ElapsedMilliseconds} мс, тишина → {(silent is null ? "нет результата (верно)" : silent.Text)}";
            });
        else Skip("PocketSphinx: декодер", "модель не указана (--model)");

        if (Directory.Exists(voskModel))
            Check("Vosk: загрузка модели и словаря команд", () =>
            {
                using var engine = new VoskEngine(voskModel);
                var spec = VocabularyBuilder.Build(new JarvisSettings(), BuiltInApps.All.Select(BuiltInApps.ToEntry), ["рабочий режим"]);
                var sw = Stopwatch.StartNew();
                engine.Initialize(spec);
                var init = sw.ElapsedMilliseconds;
                if (!engine.IsReady) throw new InvalidOperationException(engine.Status);
                sw.Restart();
                var silent = engine.Recognize(new short[32000]);
                return $"{engine.Status}, init {init} мс, 2 с тишины {sw.ElapsedMilliseconds} мс → {(silent is null ? "нет результата (верно)" : silent.Text)}";
            });
        else Skip("Vosk: модель", "модель не указана (--vosk-model)");

        Check("Windows SAPI: синтез речи", () =>
        {
            var voices = c!.Tts.GetVoices();
            return $"голосов: {voices.Count} [{string.Join("; ", voices)}], русский: {(c.Tts.HasRussianVoice ? "есть" : "нет (нужен языковой пакет)")}";
        });
        Check("Windows SAPI: распознавание (культуры)", () =>
            $"[{string.Join(", ", SapiRecognizer.InstalledCultures())}]");
        Check("Микрофоны (NAudio WinMM)", () =>
        {
            var d = c!.Audio.GetDevices();
            return d.Count == 0 ? "устройств нет (на CI это нормально)" : string.Join("; ", d.Select(x => x.Name));
        });
        Check("Окна: перечисление (EnumWindows)", () =>
        {
            var w = c!.Windows.GetTopLevelWindows();
            return $"окон: {w.Count}";
        });

        if (Directory.Exists(tess))
            await CheckAsync("OCR Tesseract: распознавание текста на экране", async () =>
            {
                var win = new Window
                {
                    WindowStyle = WindowStyle.None, Topmost = true, Left = 40, Top = 40, Width = 900, Height = 220,
                    Background = Brushes.White, ShowActivated = true,
                    Content = new TextBlock { Text = "ПРИВЕТ ДЖАРВИС Hello", FontSize = 64, Foreground = Brushes.Black, Margin = new Thickness(30) }
                };
                win.Show();
                await Task.Delay(800);
                using var ocr = new TesseractOcr(tess, "rus+eng");
                var src = PresentationSource.FromVisual(win);
                var k = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                var words = ocr.Read(new ScreenRect((int)(40 * k), (int)(40 * k), (int)(900 * k), (int)(220 * k)), CancellationToken.None);
                win.Close();
                var text = string.Join(" ", words.Select(w => w.Text));
                if (!text.Contains("ПРИВЕТ", StringComparison.OrdinalIgnoreCase) && !text.Contains("Hello", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"текст не распознан: «{text}» ({ocr.Status})");
                return $"распознано: «{text}»";
            });
        else Skip("OCR Tesseract", "tessdata не указана (--tessdata)");

        if (desktop && c is not null)
        {
            await CheckAsync("Сквозной сценарий: «открой блокнот» через конвейер команд", async () =>
            {
                await WithTimeout(c.HandleCommandTextAsync("открой блокнот", fromVoice: false), 25);
                var w = await WaitWindow(c, "notepad", 15);
                return $"окно найдено: «{w.Title}» (pid {w.ProcessId})";
            });
            await CheckAsync("UI Automation: элементы окна блокнота", async () =>
            {
                var w = await WaitWindow(c, "notepad", 5);
                var els = await c.Vision.SnapshotElementsAsync(w.Handle, 40, CancellationToken.None);
                return $"элементов: {els.Count} [{string.Join(", ", els.Take(8).Select(e => $"{e.ControlType}:{e.Name}"))}]";
            });
            await CheckAsync("Сквозной сценарий: «сверни блокнот» / «разверни блокнот»", async () =>
            {
                await WithTimeout(c.HandleCommandTextAsync("сверни блокнот", fromVoice: false), 15);
                await Task.Delay(600);
                var w = await WaitWindow(c, "notepad", 5);
                var min = w.IsMinimized;
                await WithTimeout(c.HandleCommandTextAsync("разверни блокнот", fromVoice: false), 15);
                await Task.Delay(600);
                var w2 = await WaitWindow(c, "notepad", 5);
                if (!min) throw new InvalidOperationException("окно не свернулось");
                return $"свёрнуто: {min}, после «разверни» свёрнуто: {w2.IsMinimized}";
            });
            await CheckAsync("Сквозной сценарий: «закрой блокнот»", async () =>
            {
                await WithTimeout(c.HandleCommandTextAsync("закрой блокнот", fromVoice: false), 15);
                for (var i = 0; i < 20; i++)
                {
                    await Task.Delay(300);
                    if (!c.Windows.GetTopLevelWindows().Any(x => string.Equals(x.ProcessName, "notepad", StringComparison.OrdinalIgnoreCase)))
                        return "окно закрыто";
                }
                throw new InvalidOperationException("окно блокнота осталось открытым");
            });
            foreach (var p in Process.GetProcessesByName("notepad")) try { p.Kill(); } catch { }
        }
        else Skip("Сквозные сценарии рабочего стола", "не запрошены (--desktop)");

        if (args.Contains("--benchmark") && c is not null)
        {
            // Явный запрос бенчмарка из командной строки (аналог кнопки «Провести бенчмарк»).
            await CheckAsync("Бенчмарк: не запускался автоматически", () =>
                Task.FromResult(c.Benchmarks.SessionsStarted == 0 ? "сеансов до явного запроса: 0" : throw new InvalidOperationException("бенчмарк запускался сам")));
            if (Directory.Exists(model)) c.Settings.Update(x => x.PocketSphinxModelPath = model);
            if (Directory.Exists(voskModel)) c.Settings.Update(x => x.VoskModelPath = voskModel);
            if (Directory.Exists(tess))
                foreach (var f in Directory.GetFiles(tess, "*.traineddata")) File.Copy(f, Path.Combine(c.Paths.TessdataDir, Path.GetFileName(f)), true);
            await CheckAsync("Бенчмарк: один комплексный сеанс по запросу", async () =>
            {
                var before = System.Text.Json.JsonSerializer.Serialize(c.Settings.Current.Performance);
                var activeBefore = c.Settings.Current.ActiveProfileId;
                var r = await c.RunBenchmarkAsync(new Jarvis.Core.Performance.BenchmarkRequest(Jarvis.Core.Performance.BenchmarkTrigger.Button));
                var after = System.Text.Json.JsonSerializer.Serialize(c.Settings.Current.Performance);
                if (before != after || activeBefore != c.Settings.Current.ActiveProfileId) throw new InvalidOperationException("настройки изменились без согласия");
                if (r.Recommendation is null) throw new InvalidOperationException("нет рекомендаций");
                Line(Jarvis.Core.Performance.BenchmarkReportExporter.ToMarkdown(r));
                return $"замеров: {r.Measurements.Count}, недоступно: {r.UnavailableMeasurements.Count()}, за {r.DurationSeconds:F0} с, " +
                       $"рекомендуемый профиль: {r.Recommendation.Best?.ProfileName ?? "—"}; настройки не изменены";
            });
        }
        else Skip("Бенчмарк", "не запрошен (--benchmark)");

        try { c?.Input.ReleaseAll(); } catch { }
        Line($"ИТОГ: OK={_ok}, FAIL={_failed}, SKIP={_skipped}");
        try { File.WriteAllText(reportPath, Report.ToString(), new UTF8Encoding(true)); } catch { }
        try { Console.Out.Write(Report.ToString()); } catch { }
        try { Directory.Delete(root, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }

    private static async Task<WindowInfo> WaitWindow(JarvisController c, string process, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var w = c.Windows.GetTopLevelWindows().FirstOrDefault(x => string.Equals(x.ProcessName, process, StringComparison.OrdinalIgnoreCase));
            if (w is not null) return w;
            await Task.Delay(300);
        }
        throw new TimeoutException($"окно процесса {process} не найдено за {seconds} с");
    }

    private static async Task WithTimeout(Task t, int seconds)
    {
        if (await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(seconds))) != t)
            throw new TimeoutException($"команда не завершилась за {seconds} с");
        await t;
    }

    private static void Line(string s) => Report.AppendLine(s);
    private static void Skip(string name, string why) { _skipped++; Line($"[SKIP] {name}: {why}"); }

    private static void Check(string name, Func<string> f)
    {
        try { Line($"[OK]   {name}: {f()}"); _ok++; }
        catch (Exception ex) { Line($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}"); _failed++; }
    }

    private static async Task CheckAsync(string name, Func<Task<string>> f)
    {
        try { Line($"[OK]   {name}: {await f()}"); _ok++; }
        catch (Exception ex) { Line($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}"); _failed++; }
    }
}
