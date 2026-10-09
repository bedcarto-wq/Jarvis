namespace Jarvis.Core.Performance;

public enum Confidence { Low, Medium, High }

public static class ConfidenceNames
{
    public static string Ru(this Confidence c) => c switch { Confidence.High => "высокая", Confidence.Medium => "средняя", _ => "низкая" };
}

public sealed class ParameterRecommendation
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public double Current { get; set; }
    public double Recommended { get; set; }
    public string Reason { get; set; } = "";
    public string Effect { get; set; } = "";
    public string BasedOn { get; set; } = "";
    public Confidence Confidence { get; set; }
}

public enum MatchKind { Exact, Close, Partial, None }

public sealed class ProfileMatch
{
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public ProfileKind Kind { get; set; }
    public double Distance { get; set; }
    public MatchKind Match { get; set; }
    public List<string> Differences { get; set; } = [];
}

public sealed class RecommendationReport
{
    /// <summary>«Слабый», «Средний» или «Промежуточный».</summary>
    public string Tier { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Findings { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public List<ParameterRecommendation> Changes { get; set; } = [];
    public Dictionary<string, double> RecommendedParameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ProfileMatch? Best { get; set; }
    public List<ProfileMatch> Alternatives { get; set; } = [];
    public bool SuggestNewProfile { get; set; }
    public bool CurrentIsSuitable { get; set; }
}

/// <summary>
/// Подбор настроек по измерениям. Правила простые и объяснимые: каждое изменение ссылается
/// на конкретное измерение и уровень уверенности. Ничего не применяет.
/// </summary>
public sealed class RecommendationService
{
    // Пороги обоснованы в docs/BENCHMARK.md.
    public const double RtfSlow = 0.5, RtfMild = 0.3;
    public const double OcrSlowMs = 2500, OcrMildMs = 1200;
    public const double UiaSlowMs = 1000, UiaMildMs = 400;

    public RecommendationReport Analyze(IReadOnlyList<Measurement> m, IReadOnlyDictionary<string, double> current)
    {
        double? V(string k) => m.FirstOrDefault(x => x.Key == k)?.Value;
        var r = new RecommendationReport();
        var low = BuiltInProfiles.Low.Parameters;
        var mid = BuiltInProfiles.Medium.Parameters;
        var rec = new Dictionary<string, double>(mid, StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, (string Reason, string BasedOn, Confidence C, string Effect)>();
        void Set(string key, double value, string reason, string basedOn, Confidence c, string effect)
        {
            rec[key] = value;
            reasons[key] = (reason, basedOn, c, effect);
        }

        // ── общая оценка ресурсов ──
        int strong = 0, mild = 0;
        var cpus = V(MKeys.LogicalCpus);
        var ram = V(MKeys.RamTotalMb);
        var ramFree = V(MKeys.RamAvailableMb);
        var load = V(MKeys.CpuLoad);
        if (cpus is { } c) { if (c <= 2) { strong++; r.Findings.Add($"Логических процессоров: {c:0} — мало для параллельной работы."); } else if (c <= 4) mild++; }
        if (ram is { } t) { if (t < 4500) { strong++; r.Findings.Add($"Оперативной памяти {t / 1024:0.#} ГБ — мало."); } else if (t < 8500) mild++; }
        if (ramFree is { } f) { if (f < 1024) { strong++; r.Findings.Add($"Свободно памяти {f:0} МБ — система близка к нехватке."); } else if (f < 2048) mild++; }
        if (load is { } l && l > 70) { mild++; r.Findings.Add($"Компьютер загружен другими процессами ({l:0} % CPU во время проверки)."); }
        var weak = strong > 0 || mild >= 2;
        var basis = $"CPU: {(cpus is null ? "недоступно" : cpus.Value.ToString("0"))} потоков; ОЗУ: {(ram is null ? "недоступно" : (ram.Value / 1024).ToString("0.#") + " ГБ")}; свободно: {(ramFree is null ? "недоступно" : ramFree.Value.ToString("0") + " МБ")}";
        var resConf = cpus is null || ram is null ? Confidence.Low : Confidence.Medium;
        if (weak)
        {
            Set("hud.animations", 0, "Ограниченные ресурсы: анимации HUD не нужны для работы.", basis, resConf, "Меньше перерисовок в фоне.");
            Set("hud.refreshMs", low["hud.refreshMs"], "Реже обновлять индикатор микрофона.", basis, resConf, "Меньше нагрузка на поток интерфейса.");
            Set("bg.maxTasks", 1, "Последовательные фоновые задачи на слабом ПК не конкурируют с вашими программами.", basis, resConf, "Ниже пиковая нагрузка.");
            Set("mon.intervalMs", low["mon.intervalMs"], "Реже измерять показатели мониторинга.", basis, resConf, "Меньше фоновых измерений.");
            Set("exec.launchTimeoutMs", low["exec.launchTimeoutMs"], "Программы на слабом ПК запускаются дольше; JARVIS не должен считать их неработающими.", basis, resConf, "Меньше ложных «программа не запустилась».");
            Set("exec.windowPollMs", low["exec.windowPollMs"], "Реже проверять появление окна.", basis, resConf, "Меньше нагрузки при ожидании.");
            Set("exec.stepDelayMs", low["exec.stepDelayMs"], "Больше времени программам на отрисовку между шагами.", basis, resConf, "Надёжнее сценарии.");
            Set("exec.typingDelayMs", low["exec.typingDelayMs"], "Медленные программы теряют символы при быстром вводе.", basis, resConf, "Надёжнее ввод текста.");
            Set("exec.elementWaitTimeoutMs", low["exec.elementWaitTimeoutMs"], "Элементы появляются дольше.", basis, resConf, "Меньше ложных тайм-аутов.");
            Set("ui.windowCacheMs", low["ui.windowCacheMs"], "Повторно использовать список окон.", basis, resConf, "Меньше системных вызовов.");
        }

        // ── распознавание речи ──
        var rtf = V(MKeys.VoiceRtf);
        if (rtf is null) r.Limitations.Add("Скорость распознавания речи не измерена — параметры распознавателя оставлены без изменений.");
        else if (rtf > RtfSlow)
        {
            Set("voice.maxHmmPf", low["voice.maxHmmPf"], $"Распознавание идёт медленно: {rtf:0.00} с обработки на 1 с речи.", $"Распознавание тестового аудио: RTF {rtf:0.00}", Confidence.High, "Быстрее ответ на команду; возможна небольшая потеря точности.");
            Set("voice.beamExp", low["voice.beamExp"], "Сузить луч поиска.", $"RTF {rtf:0.00}", Confidence.High, "Меньше вычислений на кадр.");
            r.Findings.Add($"Распознаватель обрабатывает 1 с речи за {rtf:0.00} с — это медленно.");
        }
        else if (rtf > RtfMild)
        {
            Set("voice.maxHmmPf", 2000, $"Распознавание на грани комфортного: RTF {rtf:0.00}.", $"RTF {rtf:0.00}", Confidence.Medium, "Чуть быстрее ответ.");
        }

        // ── OCR ──
        var ocr2 = V(MKeys.OcrMsScale2);
        var ocr15 = V(MKeys.OcrMsScale15);
        if (ocr2 is null) r.Limitations.Add("OCR не измерен (нет языковых файлов или библиотек) — параметры OCR без изменений.");
        else if (ocr2 > OcrSlowMs)
        {
            var target = ocr15 is { } o && o < OcrSlowMs ? 1.5 : 1.0;
            Set("ocr.scale", target, $"OCR с увеличением 2× занимает {ocr2:0} мс на тестовое окно 1280×720.", $"OCR тестового изображения: {ocr2:0} мс (2×), {(ocr15 is null ? "—" : ocr15.Value.ToString("0") + " мс (1,5×)")}", Confidence.High, "OCR быстрее; мелкий текст может распознаваться хуже.");
            Set("ocr.maxWindows", 1, "Распознавать только одно окно-кандидат.", $"OCR {ocr2:0} мс", Confidence.High, "Поиск по тексту не будет занимать секунды.");
        }
        else if (ocr2 > OcrMildMs)
        {
            Set("ocr.scale", 1.75, $"OCR 2× — {ocr2:0} мс.", $"OCR {ocr2:0} мс", Confidence.Medium, "Немного быстрее OCR.");
            Set("ocr.maxWindows", 2, "Меньше окон для OCR за один поиск.", $"OCR {ocr2:0} мс", Confidence.Medium, "Быстрее поиск по тексту.");
        }

        // ── UI Automation ──
        var uia = V(MKeys.UiaSnapshotMs);
        if (uia is null) r.Limitations.Add("UI Automation не измерена — параметры поиска элементов без изменений.");
        else if (uia > UiaSlowMs)
        {
            Set("ui.uiaMaxElements", low["ui.uiaMaxElements"], $"Обход 200 элементов тестового окна занял {uia:0} мс.", $"UI Automation: {uia:0} мс", Confidence.High, "Поиск элемента не будет «подвешивать» компьютер.");
            Set("ui.uiaWalkTimeMs", low["ui.uiaWalkTimeMs"], "Дать обходу больше времени, но меньше элементов.", $"UI Automation: {uia:0} мс", Confidence.Medium, "Меньше ложных «элемент не найден».");
            Set("exec.elementPollMs", low["exec.elementPollMs"], "Реже повторять дорогой поиск элемента.", $"UI Automation: {uia:0} мс", Confidence.High, "Меньше нагрузки при ожидании элемента.");
            Set("ui.windowCacheMs", low["ui.windowCacheMs"], "Повторно использовать список окон.", $"UI Automation: {uia:0} мс", Confidence.Medium, "Меньше системных вызовов.");
        }
        else if (uia > UiaMildMs)
        {
            Set("ui.uiaMaxElements", 2500, $"Обход элементов: {uia:0} мс.", $"UI Automation: {uia:0} мс", Confidence.Medium, "Быстрее нечёткий поиск.");
            Set("exec.elementPollMs", 750, "Чуть реже проверять элемент.", $"UI Automation: {uia:0} мс", Confidence.Medium, "Меньше нагрузки.");
        }

        // ── диск ──
        var w = V(MKeys.DiskSmallWriteMs);
        if (w is { } wm && wm > 30)
            r.Findings.Add($"Запись небольших файлов в папку данных медленная ({wm:0} мс). На работу команд это почти не влияет: настройки и шаблоны читаются в память при запуске.");
        if (m.FirstOrDefault(x => x.Key == MKeys.DiskKind)?.Text is { } kind)
            r.Notes.Add($"Тип системного диска: {kind}. Тип диска не используется как самостоятельный критерий — учитываются измеренные задержки.");

        foreach (var x in m.Where(x => !x.Available)) r.Limitations.Add($"{x.Title}: недоступно — {x.Note}");

        r.RecommendedParameters = rec;
        foreach (var d in PerformanceCatalog.All)
        {
            var cur = current.TryGetValue(d.Key, out var cv) ? cv : mid[d.Key];
            var nv = rec[d.Key];
            if (d.Distance(cur, nv) < 1e-9) continue;
            var (reason, basedOn, conf, effect) = reasons.TryGetValue(d.Key, out var why)
                ? why
                : ("Значение профиля «Средний ПК»: измерения не показали необходимости в другом значении.", "Совокупность измерений", Confidence.Low, "Возврат к сбалансированному значению.");
            r.Changes.Add(new ParameterRecommendation
            {
                Key = d.Key, Label = d.Label, Current = cur, Recommended = nv, Reason = reason, BasedOn = basedOn, Confidence = conf, Effect = effect,
            });
        }

        r.Tier = strong > 0 ? "Слабый" : mild >= 2 || reasons.Count > 0 ? "Промежуточный" : "Средний";
        r.Summary = r.Tier switch
        {
            "Слабый" => "Компьютер ограничен по ресурсам. Рекомендуются экономные настройки — все функции останутся доступными.",
            "Промежуточный" => "Ресурсов в целом достаточно, но отдельные компоненты работают медленнее обычного.",
            _ => "Ресурсов достаточно для сбалансированных настроек.",
        };
        if (r.Limitations.Count > 0) r.Summary += " Часть показателей недоступна — рекомендация приблизительная.";
        return r;
    }

    /// <summary>Сопоставляет рекомендованные значения с существующими профилями (базовыми и пользовательскими).</summary>
    public void Match(RecommendationReport r, IReadOnlyList<PerformanceProfile> profiles, string? activeProfileId)
    {
        var matches = profiles.Select(p =>
        {
            var dist = ProfileComparisonService.Distance(r.RecommendedParameters, p.Parameters);
            var diffs = ProfileComparisonService.Compare(p.Parameters, r.RecommendedParameters).Where(x => !x.Equal)
                .Select(x => $"{x.Parameter.Label}: {x.Parameter.Format(x.Left)} → {x.Parameter.Format(x.Right)}").ToList();
            return new ProfileMatch
            {
                ProfileId = p.Id, ProfileName = p.Name, Kind = p.Kind, Distance = dist, Differences = diffs,
                Match = diffs.Count == 0 ? MatchKind.Exact : dist <= 0.03 ? MatchKind.Close : dist <= 0.10 ? MatchKind.Partial : MatchKind.None,
            };
        }).OrderBy(x => x.Distance).ThenBy(x => x.Kind).ToList();
        r.Best = matches.FirstOrDefault();
        r.Alternatives = matches.Skip(1).Where(x => x.Match != MatchKind.None).Take(3).ToList();
        r.SuggestNewProfile = r.Best is null || r.Best.Match is MatchKind.Partial or MatchKind.None;
        r.CurrentIsSuitable = r.Changes.Count == 0 || (activeProfileId is not null && r.Best?.ProfileId == activeProfileId && r.Best.Match == MatchKind.Exact);
    }
}
