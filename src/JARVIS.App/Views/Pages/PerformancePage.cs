using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Jarvis.App.Services;
using Jarvis.Core.Performance;
using Jarvis.Core.Settings;
using Microsoft.Win32;
using VM = Jarvis.Core.Performance.PerformanceSettingsViewModel;

namespace Jarvis.App.Views.Pages;

/// <summary>
/// Страница «Производительность»: активный профиль, базовые профили и пресеты, пользовательские профили,
/// бенчмарк (только по нажатию кнопки или голосовой команде с подтверждением), результаты, мониторинг.
/// </summary>
public sealed class PerformancePage : UserControl, IPage
{
    private readonly JarvisController _c;
    private readonly ContentControl _active = new(), _builtin = new(), _presets = new(), _user = new(), _results = new(), _monitor = new();
    private readonly TextBlock _stage = K.Text(""), _probeList = K.Hint("");
    private readonly ProgressBar _progress = new() { Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 4), Visibility = Visibility.Collapsed };
    private readonly Button _runButton, _cancelButton;
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromSeconds(2) };
    private BenchmarkResult? _shown;

    public PerformancePage(JarvisController c)
    {
        _c = c;
        var s = _c.Settings.Current;
        void U(Action<JarvisSettings> a) => _c.Settings.Update(a);

        _runButton = K.Btn(VM.BenchmarkButton, () => _ = StartBenchmarkAsync(BenchmarkTrigger.Button), "GoldButton");
        _runButton.FontSize = 15;
        _runButton.Padding = new Thickness(18, 8, 18, 8);
        _cancelButton = K.Btn("Отменить бенчмарк", () => _c.CancelBenchmark(), "DangerButton");
        _cancelButton.Visibility = Visibility.Collapsed;

        _c.Profiles.Changed += () => Dispatcher.BeginInvoke(RefreshProfiles);
        _c.Settings.Changed += _ => Dispatcher.BeginInvoke(RefreshActive);
        _c.Benchmarks.Progress += p => Dispatcher.BeginInvoke(() => ShowProgress(p));
        _ui.Tick += (_, _) => RefreshMonitor();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) _ui.Start(); else _ui.Stop(); };

        Content = K.Page(
            K.H1("Производительность"),
            K.Card(K.H2("Активный профиль"), _active),
            K.Card(K.H2("Базовые профили"), _builtin),
            K.Card(K.H2("Готовые пресеты"), K.Hint("Пресеты — отправная точка. Их можно применить или скопировать в свой профиль."), _presets),
            K.Card(K.H2("Пользовательские профили"), _user,
                K.Row(K.Btn("Создать профиль", () => CreateProfile(null)), K.Btn("Импортировать…", ImportProfile),
                      K.Btn("Сравнить профили…", () => PerfDialogs.Compare(_c.Profiles.All)))),
            K.Card(K.H2("Бенчмарк"), K.Text(VM.BenchmarkExplanation),
                K.Row(_runButton, _cancelButton, K.Btn("Открыть последний отчёт", OpenLastReport)),
                _progress, _stage, _probeList),
            K.Card(K.H2("Результаты"), _results),
            K.Card(K.H2("Мониторинг"),
                K.Check("Включить лёгкий мониторинг (CPU и память JARVIS, активный профиль, текущая задача)", s.MonitoringEnabled,
                    v => U(x => x.MonitoringEnabled = v), "Мониторинг не запускает бенчмарк и не делает дорогих замеров."),
                _monitor),
            K.Card(K.H2("Хранение данных"),
                K.Labeled("Дополнительное место для отчётов и экспорта (например, папка на HDD)",
                    K.Box(s.ExtraStoragePath, v => { U(x => x.ExtraStoragePath = string.IsNullOrWhiteSpace(v) ? null : v.Trim()); RefreshStorage(); }, 420),
                    "Если папка недоступна, JARVIS сохранит данные в локальную папку и сообщит об этом."),
                _storage,
                K.Row(K.Btn("Открыть папку данных", () => OpenFolder(_c.Paths.Root)), K.Btn("Открыть папку отчётов", () => OpenFolder(_c.Paths.ReportsDir)))),
            K.Card(K.H2("HUD"),
                K.Check("Показывать HUD", s.HudEnabled, v => U(x => x.HudEnabled = v)),
                K.Labeled("Положение", K.Combo(new (HudCorner, string)[]
                {
                    (HudCorner.TopRight, "Справа сверху"), (HudCorner.TopLeft, "Слева сверху"),
                    (HudCorner.BottomRight, "Справа снизу"), (HudCorner.BottomLeft, "Слева снизу"),
                }, s.HudCorner, v => U(x => x.HudCorner = v))),
                K.Labeled("Непрозрачность (0,3…1)", K.Number(s.HudOpacity, v => U(x => x.HudOpacity = v), 0.3, 1)),
                K.Hint("Анимации и частота обновления HUD задаются профилем.")),
            K.Card(K.H2("Надёжность выполнения (не входит в профиль)"),
                K.Labeled("Повторов шага по умолчанию", K.Number(s.DefaultRetries, v => U(x => x.DefaultRetries = (int)v), 0, 10)),
                K.Labeled("Максимум повторов цикла", K.Number(s.MaxRepeatCount, v => U(x => x.MaxRepeatCount = (int)v), 1, 10000)),
                K.Labeled("Максимальная вложенность шаблонов", K.Number(s.MaxTemplateNesting, v => U(x => x.MaxTemplateNesting = (int)v), 1, 32))),
            K.Card(K.H2("Зрение"),
                K.Check("UI Automation (основной способ, быстрый и точный)", s.VisionUseUiAutomation, v => U(x => x.VisionUseUiAutomation = v)),
                K.Check("OCR Tesseract (медленнее, только когда UI Automation не помог)", s.VisionUseOcr, v => U(x => x.VisionUseOcr = v)),
                K.Labeled("Языки OCR", K.Box(s.OcrLanguages, v => U(x => x.OcrLanguages = v.Trim()), 200)),
                K.Check("Разрешить координаты как последний вариант", s.VisionAllowCoordinateFallback, v => U(x => x.VisionAllowCoordinateFallback = v)),
                K.Hint(_c.Vision.OcrStatus)));

        RefreshProfiles();
        RefreshStorage();
        _results.Content = K.Hint("Бенчмарк ещё не проводился в этом сеансе. Можно открыть последний сохранённый отчёт.");
    }

    private readonly TextBlock _storage = K.Hint("");

    public void OnShown()
    {
        RefreshActive();
        RefreshMonitor();
    }

    // ───────── профили ─────────

    private void RefreshProfiles()
    {
        RefreshActive();
        _builtin.Content = ProfileCards(_c.Profiles.BuiltIn);
        _presets.Content = ProfileCards(_c.Profiles.Presets);
        var users = _c.Profiles.User;
        _user.Content = users.Count == 0
            ? K.Hint("Своих профилей пока нет. Создайте профиль на основе базового или сохраните рекомендации бенчмарка.")
            : ProfileCards(users);
    }

    private void RefreshActive()
    {
        var s = _c.Settings.Current;
        var p = _c.Profiles.Get(s.ActiveProfileId);
        var cur = PerformanceCatalog.Read(s);
        _active.Content = K.Stack(
            K.Text(_c.ActiveProfileTitle, 16),
            K.Hint(p?.Description ?? "Параметры изменены вручную или применены рекомендации бенчмарка без сохранения в профиль."),
            PerfDialogs.Table(["Основной параметр", "Значение"], VM.MainParameters(cur).Select(x => new[] { x.Label, x.Value }), [3, 2]),
            K.Row(K.Btn("Изменить профиль…", ChangeProfile), K.Btn("Создать новый профиль", () => CreateProfile(p?.Id)),
                  K.Btn("Сохранить текущие значения как профиль", SaveCurrentAsProfile)));
    }

    private UIElement ProfileCards(IReadOnlyList<PerformanceProfile> list)
    {
        var sp = new StackPanel();
        foreach (var p in list)
        {
            var isActive = p.Id == _c.Settings.Current.ActiveProfileId;
            var header = K.Text(p.Name + (isActive ? "  — активен" : ""), 15);
            header.FontWeight = FontWeights.SemiBold;
            var inner = K.Stack(header);
            if (p.Purpose is { } purpose) inner.Children.Add(K.Text("Назначение: " + purpose));
            if (p.Description is { } d) inner.Children.Add(K.Hint(d));
            inner.Children.Add(K.Hint(string.Join(" · ", VM.MainParameters(p.Parameters).Select(x => $"{x.Label}: {x.Value}"))));
            var row = K.Row();
            if (!isActive) row.Children.Add(K.Btn(p.Kind == ProfileKind.User ? "Активировать" : "Выбрать", () => _ = ApplyAsync(p.Id), "GoldButton"));
            row.Children.Add(K.Btn(p.IsReadOnly ? "Просмотреть" : "Редактировать", () => EditProfile(p)));
            row.Children.Add(K.Btn("Копировать", () => CopyProfile(p)));
            if (p.Kind == ProfileKind.User)
            {
                row.Children.Add(K.Btn("Переименовать", () => RenameProfile(p)));
                if (p.BasedOn is not null) row.Children.Add(K.Btn("Сбросить к исходному", () => ResetProfile(p)));
            }
            row.Children.Add(K.Btn("Экспортировать", () => ExportProfile(p)));
            row.Children.Add(K.Btn("Сравнить с текущим", () => PerfDialogs.Compare(_c.Profiles.All, p, _c.Profiles.Get(_c.Settings.Current.ActiveProfileId))));
            if (p.Kind == ProfileKind.User) row.Children.Add(K.Btn("Удалить", () => DeleteProfile(p), "DangerButton"));
            inner.Children.Add(row);
            sp.Children.Add(new Border { Child = inner, BorderBrush = K.B("Border"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6, 0, 10) });
        }
        return sp;
    }

    private async Task ApplyAsync(string id)
    {
        var p = _c.Profiles.Get(id);
        if (p is null) return;
        var changes = _c.Profiles.PreviewChanges(id, _c.Settings.Current);
        if (changes.Count > 0 && !K.Ask($"Применить профиль «{p.Name}»?\n\nИзменится параметров: {changes.Count}\n" +
                                         string.Join("\n", changes.Take(12).Select(x => $"• {x.Parameter.Label}: {x.Parameter.Format(x.Left)} → {x.Parameter.Format(x.Right)}"))))
            return;
        var r = await _c.ApplyProfileAsync(id);
        Feedback(r.Message, r.Success);
        RefreshProfiles();
    }

    private void ChangeProfile()
    {
        var p = PerfDialogs.ChooseProfile("Выбор профиля", _c.Profiles.All);
        if (p is not null) _ = ApplyAsync(p.Id);
    }

    private void CreateProfile(string? basedOn)
    {
        var name = PerfDialogs.Prompt("Новый профиль", "Название профиля");
        if (name is null) return;
        var baseId = basedOn ?? _c.Profiles.Get(_c.Settings.Current.ActiveProfileId)?.Id ?? BuiltInProfiles.MediumId;
        Try(() =>
        {
            var p = _c.Profiles.Create(name, baseId);
            EditProfile(p);
        });
    }

    private void SaveCurrentAsProfile()
    {
        var name = PerfDialogs.Prompt("Сохранить как профиль", "Название профиля", "Мои настройки");
        if (name is null) return;
        Try(() =>
        {
            var p = _c.Profiles.CreateFromValues(name, PerformanceCatalog.Read(_c.Settings.Current), "Сохранено из текущих настроек.",
                _c.Profiles.Get(_c.Settings.Current.ActiveProfileId)?.Id);
            Feedback($"Профиль «{p.Name}» сохранён. Он не применён — нажмите «Активировать», если нужно.", true);
        });
    }

    private void EditProfile(PerformanceProfile p)
    {
        if (p.IsReadOnly)
        {
            var r = PerfDialogs.EditProfile(p, nameEditable: true);
            if (r is null) return;
            // Встроенные профили не изменяются: правки сохраняются в новую копию.
            Try(() =>
            {
                var copy = _c.Profiles.CreateFromValues(r.Value.Name == p.Name ? p.Name + " (копия)" : r.Value.Name, r.Value.Values, r.Value.Description, p.Id);
                Feedback($"Встроенный профиль не изменён. Создана копия «{copy.Name}».", true);
            });
            return;
        }
        var res = PerfDialogs.EditProfile(p, nameEditable: true);
        if (res is null) return;
        Try(() =>
        {
            if (res.Value.Name != p.Name) _c.Profiles.Rename(p.Id, res.Value.Name);
            var errors = _c.Profiles.UpdateValues(p.Id, res.Value.Values, res.Value.Description);
            if (errors.Count > 0) { Feedback("Не сохранено: " + string.Join(" ", errors), false); return; }
            if (p.Id == _c.Settings.Current.ActiveProfileId && K.Ask("Профиль активен. Применить изменённые значения сейчас?")) _ = ApplyAsync(p.Id);
            else Feedback($"Профиль «{res.Value.Name}» сохранён.", true);
        });
    }

    private void CopyProfile(PerformanceProfile p)
    {
        var name = PerfDialogs.Prompt("Копия профиля", "Название копии", p.Name + " (копия)");
        if (name is null) return;
        Try(() => { var c = _c.Profiles.Copy(p.Id, name); Feedback($"Создана копия «{c.Name}».", true); });
    }

    private void RenameProfile(PerformanceProfile p)
    {
        var name = PerfDialogs.Prompt("Переименование", "Новое название", p.Name);
        if (name is null) return;
        Try(() => _c.Profiles.Rename(p.Id, name));
    }

    private void ResetProfile(PerformanceProfile p)
    {
        if (!K.Ask($"Вернуть профилю «{p.Name}» значения исходного профиля? Название сохранится.")) return;
        Try(() => _c.Profiles.ResetToOriginal(p.Id));
    }

    private void DeleteProfile(PerformanceProfile p)
    {
        if (!K.Ask($"Удалить профиль «{p.Name}»? Это действие нельзя отменить.")) return;
        Try(() =>
        {
            _c.Profiles.Delete(p.Id);
            Feedback($"Профиль «{p.Name}» удалён." + (p.Id == _c.Settings.Current.ActiveProfileId ? " Текущие значения настроек сохранены." : ""), true);
        });
    }

    private void ExportProfile(PerformanceProfile p)
    {
        var dir = _c.ResolveExportDir();
        var dlg = new SaveFileDialog
        {
            Title = "Экспорт профиля", Filter = "Профиль JARVIS (*.json)|*.json", InitialDirectory = dir.Path,
            FileName = string.Concat(p.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) + ".json",
        };
        if (dlg.ShowDialog() != true) return;
        Try(() => { _c.Profiles.Export(p.Id, dlg.FileName); Feedback($"Профиль экспортирован: {dlg.FileName}" + (dir.Message is null ? "" : $" ({dir.Message})"), true); });
    }

    private void ImportProfile()
    {
        var dlg = new OpenFileDialog { Title = "Импорт профиля", Filter = "Профиль JARVIS (*.json)|*.json|Все файлы|*.*", InitialDirectory = _c.ResolveExportDir().Path };
        if (dlg.ShowDialog() != true) return;
        var v = _c.Profiles.ValidateFile(dlg.FileName);
        if (!PerfDialogs.ImportPreview(v)) return;
        Try(() => { var p = _c.Profiles.Import(v); Feedback($"Профиль «{p.Name}» импортирован. Он не применён автоматически.", true); });
    }

    // ───────── бенчмарк ─────────

    /// <summary>Вызывается голосовой командой: показывает то же предупреждение, что и кнопка.</summary>
    public void RequestBenchmark(BenchmarkTrigger trigger) => _ = StartBenchmarkAsync(trigger);

    private async Task StartBenchmarkAsync(BenchmarkTrigger trigger)
    {
        if (_c.BenchmarkRunning) return;
        var (start, mic) = PerfDialogs.BenchmarkWarning(_c);
        if (!start) { _stage.Text = "Бенчмарк отменён пользователем. Ничего не запускалось."; return; }
        _runButton.IsEnabled = false;
        _cancelButton.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _probeList.Text = "";
        try
        {
            var r = await _c.RunBenchmarkAsync(new BenchmarkRequest(trigger, mic));
            ShowResult(r);
            _stage.Text = r.Cancelled ? "Бенчмарк отменён. Настройки не изменены." : $"Бенчмарк завершён за {r.DurationSeconds:F0} с. Настройки не изменены — выберите действие ниже.";
            _probeList.Text = string.Join("\n", r.Probes.Select(p => $"• {p.Title}: {p.Status} ({p.DurationMs / 1000:F1} с){(p.Error is null ? "" : " — " + p.Error)}"));
        }
        catch (Exception ex) { _stage.Text = "Бенчмарк не выполнен: " + ex.Message; }
        finally
        {
            _runButton.IsEnabled = true;
            _cancelButton.Visibility = Visibility.Collapsed;
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowProgress(BenchmarkProgress p)
    {
        _stage.Text = VM.ProgressText(p);
        if (p.Total > 0) _progress.Value = (double)p.Completed / p.Total;
    }

    private void OpenLastReport()
    {
        var r = _c.Reports.LoadLatest();
        if (r is null) { _results.Content = K.Hint("Сохранённых отчётов нет. Проведите бенчмарк."); return; }
        ShowResult(r, saved: true);
    }

    private void ShowResult(BenchmarkResult r, bool saved = false)
    {
        _shown = r;
        var sp = new StackPanel();
        sp.Children.Add(K.Text($"Отчёт от {r.StartedAt:dd.MM.yyyy HH:mm}, версия {r.AppVersion}, длительность {r.DurationSeconds:F0} с", 14));
        if (saved) { var w = K.Hint(VM.StaleReportWarning); w.Foreground = K.B("Warn"); sp.Children.Add(w); }
        if (r.Cancelled) sp.Children.Add(K.Text("Бенчмарк был отменён — рекомендации не формировались."));
        var rec = r.Recommendation;
        if (rec is not null)
        {
            sp.Children.Add(K.H2("Итог"));
            sp.Children.Add(Wrap(K.Text(rec.Summary)));
            foreach (var f in rec.Findings) sp.Children.Add(Wrap(K.Text("• " + f)));
            sp.Children.Add(K.H2("Рекомендованный профиль"));
            if (rec.Best is { } b)
                sp.Children.Add(Wrap(K.Text($"{b.ProfileName} — {BenchmarkReportExporter.MatchRu(b.Match)}" +
                                            (b.Differences.Count > 0 ? $"\nОтличия: {string.Join("; ", b.Differences)}" : ""))));
            if (rec.SuggestNewProfile) sp.Children.Add(K.Hint("Ни один профиль точно не совпадает — можно сохранить рекомендации как новый профиль."));
            if (rec.CurrentIsSuitable) sp.Children.Add(K.Hint("Текущие настройки уже подходят для этого компьютера."));
            if (rec.Changes.Count > 0)
            {
                sp.Children.Add(K.H2("Причины и сравнение параметров"));
                sp.Children.Add(PerfDialogs.Table(["Параметр", "Сейчас", "Рекомендуется", "Почему", "Что изменится", "Основание", "Уверенность"],
                    rec.Changes.Select(c =>
                    {
                        var d = PerformanceCatalog.Find(c.Key);
                        return new[] { c.Label, d?.Format(c.Current) ?? c.Current.ToString(), d?.Format(c.Recommended) ?? c.Recommended.ToString(),
                            c.Reason, c.Effect, c.BasedOn, c.Confidence.Ru() };
                    }), [2, 1, 1.2, 3, 2.5, 2, 1]));
            }
            if (rec.Alternatives.Count > 0)
            {
                sp.Children.Add(K.H2("Подходящие альтернативы"));
                foreach (var a in rec.Alternatives) sp.Children.Add(K.Text($"• {a.ProfileName} — {BenchmarkReportExporter.MatchRu(a.Match)}"));
            }
            if (rec.Limitations.Count > 0)
            {
                sp.Children.Add(K.H2("Ограничения проверки"));
                foreach (var l in rec.Limitations) sp.Children.Add(Wrap(K.Hint("• " + l)));
            }
        }
        sp.Children.Add(K.H2("Измерения"));
        sp.Children.Add(PerfDialogs.Table(["Группа", "Показатель", "Значение", "Примечание"],
            r.Measurements.Select(m => new[] { m.Group, m.Title, m.Display, m.Note ?? "" }), [1.3, 2.5, 1.5, 2.5]));

        var row = K.Row();
        foreach (var a in VM.AvailableActions(r))
            row.Children.Add(K.Btn(VM.ActionRu(a), () => _ = OnResultAction(a), a is BenchmarkResultAction.UseRecommendedProfile or BenchmarkResultAction.ApplyRecommendedSettings ? "GoldButton" : null));
        sp.Children.Add(row);
        _results.Content = sp;
    }

    private static TextBlock Wrap(TextBlock t) { t.TextWrapping = TextWrapping.Wrap; return t; }

    private async Task OnResultAction(BenchmarkResultAction a)
    {
        var r = _shown;
        var rec = r?.Recommendation;
        switch (a)
        {
            case BenchmarkResultAction.UseRecommendedProfile when rec?.Best is { } b:
                await ApplyAsync(b.ProfileId);
                break;
            case BenchmarkResultAction.ChooseOtherProfile:
                ChangeProfile();
                break;
            case BenchmarkResultAction.ApplyRecommendedSettings when rec is not null:
                if (!K.Ask("Применить рекомендованные значения?\n\n" + string.Join("\n", rec.Changes.Select(c =>
                        $"• {c.Label}: {PerformanceCatalog.Find(c.Key)?.Format(c.Current)} → {PerformanceCatalog.Find(c.Key)?.Format(c.Recommended)}")) +
                        "\n\nТекущий профиль не будет перезаписан.")) return;
                var res = await _c.ApplyValuesAsync(rec.RecommendedParameters, "Рекомендации бенчмарка");
                Feedback(res.Message.Replace("Профиль «Рекомендации бенчмарка» применён", "Рекомендованные настройки применены"), res.Success);
                RefreshProfiles();
                break;
            case BenchmarkResultAction.SaveAsNewProfile when rec is not null:
                var name = PerfDialogs.Prompt("Новый профиль из рекомендаций", "Название профиля", $"Рекомендации {r!.StartedAt:dd.MM.yyyy}");
                if (name is null) return;
                Try(() =>
                {
                    var p = _c.Profiles.CreateFromValues(name, rec.RecommendedParameters, $"Создан по результатам бенчмарка от {r.StartedAt:dd.MM.yyyy HH:mm}.", rec.Best?.ProfileId);
                    Feedback($"Профиль «{p.Name}» сохранён, но не применён. Нажмите «Активировать», если хотите его использовать.", true);
                });
                break;
            case BenchmarkResultAction.CopyExistingProfile:
                var src = PerfDialogs.ChooseProfile("Какой профиль скопировать?", _c.Profiles.All);
                if (src is not null) CopyProfile(src);
                break;
            case BenchmarkResultAction.KeepCurrent:
                Feedback("Текущие настройки оставлены без изменений.", true);
                break;
            case BenchmarkResultAction.ExportReport when r is not null:
                ExportReport(r);
                break;
            case BenchmarkResultAction.Close:
                _results.Content = K.Hint("Результаты закрыты. Их можно снова открыть кнопкой «Открыть последний отчёт».");
                _shown = null;
                break;
        }
    }

    private void ExportReport(BenchmarkResult r)
    {
        var dir = _c.ResolveExportDir();
        var dlg = new SaveFileDialog
        {
            Title = "Экспорт отчёта", Filter = "Отчёт Markdown (*.md)|*.md|Данные JSON (*.json)|*.json", InitialDirectory = dir.Path,
            FileName = $"JARVIS-benchmark-{r.StartedAt:yyyyMMdd-HHmm}.md",
        };
        if (dlg.ShowDialog() != true) return;
        Try(() =>
        {
            var text = dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? System.Text.Json.JsonSerializer.Serialize(r, new System.Text.Json.JsonSerializerOptions
                    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                : BenchmarkReportExporter.ToMarkdown(r);
            File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(true));
            Feedback($"Отчёт сохранён: {dlg.FileName}" + (dir.Message is null ? "" : $" ({dir.Message})"), true);
        });
    }

    // ───────── мониторинг и хранилище ─────────

    private void RefreshMonitor()
    {
        if (!_c.Settings.Current.MonitoringEnabled)
        {
            _monitor.Content = K.Hint("Мониторинг выключен.");
            return;
        }
        var m = _c.Monitoring.Last ?? _c.Monitoring.SampleNow();
        _monitor.Content = K.Stack(
            K.Text($"Активный профиль: {m.ActiveProfile}"),
            K.Text($"Текущая задача: {m.CurrentTask}"),
            K.Text($"Процессор (JARVIS): {VM.Metric(m.CpuPercent, "%")}"),
            K.Text($"Память (рабочий набор): {VM.Metric(m.WorkingSetMb, "МБ", "0")}; выделено: {VM.Metric(m.PrivateMb, "МБ", "0")}"),
            K.Text($"Потоков: {(m.Threads is { } t ? t.ToString() : VM.Unavailable)}"),
            K.Text($"Распознаватель: {_c.Engine?.Name ?? "не запущен"}; последняя команда: {(_c.LastCommandMs > 0 ? $"{_c.LastCommandMs:F0} мс" : VM.Unavailable)}"),
            K.Hint($"Обновлено {m.At:HH:mm:ss}. Интервал задаётся профилем."));
    }

    private void RefreshStorage()
    {
        var extra = _c.Settings.Current.ExtraStoragePath;
        if (string.IsNullOrWhiteSpace(extra)) { _storage.Text = $"Данные: {_c.Paths.Root}"; return; }
        var (ok, msg) = StorageLocations.Probe(extra);
        _storage.Text = ok ? $"Дополнительное место доступно: {extra}" : $"Дополнительное место недоступно ({msg}). Используется {_c.Paths.Root}.";
    }

    private static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { K.Info("Не удалось открыть папку: " + ex.Message); }
    }

    private void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { Feedback(ex.Message, false); }
    }

    private void Feedback(string text, bool ok)
    {
        _c.Reply(text, ok ? Jarvis.Core.Abstractions.NotifyLevel.Success : Jarvis.Core.Abstractions.NotifyLevel.Warning, speak: false);
        if (!ok) K.Info(text);
        else _stage.Text = text;
    }
}
