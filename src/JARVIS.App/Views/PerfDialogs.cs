using System.Windows;
using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.App.Views.Pages;
using Jarvis.Core.Performance;

namespace Jarvis.App.Views;

/// <summary>Окна страницы «Производительность»: ввод имени, предупреждение бенчмарка, редактор, сравнение, импорт.</summary>
internal static class PerfDialogs
{
    public static Grid Table(string[] headers, IEnumerable<string[]> rows, double[]? widths = null)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        for (var i = 0; i < headers.Length; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = widths is null ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i], GridUnitType.Star) });
        var all = new List<string[]> { headers };
        all.AddRange(rows);
        for (var r = 0; r < all.Count; r++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < headers.Length; c++)
            {
                var tb = new TextBlock
                {
                    Text = c < all[r].Length ? all[r][c] : "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 10, 2),
                    FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                };
                if (r == 0) tb.Foreground = K.B("Muted");
                Grid.SetRow(tb, r);
                Grid.SetColumn(tb, c);
                g.Children.Add(tb);
            }
        }
        return g;
    }

    private static (Window W, StackPanel Body) Frame(string title, double width)
    {
        var w = Ui.StyledWindow(title, width);
        w.Topmost = false;
        w.Owner = Application.Current.MainWindow is { IsVisible: true } m ? m : null;
        if (w.Owner is not null) w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var sp = new StackPanel { Margin = new Thickness(18) };
        w.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 720 };
        return (w, sp);
    }

    private static WrapPanel Buttons(params Button[] buttons)
    {
        var p = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) { b.Margin = new Thickness(6, 0, 0, 0); p.Children.Add(b); }
        return p;
    }

    public static string? Prompt(string title, string label, string initial = "")
    {
        var (w, sp) = Frame(title, 440);
        var box = K.Box(initial);
        string? result = null;
        sp.Children.Add(new Label { Content = label });
        sp.Children.Add(box);
        var ok = K.Btn("Сохранить", () => { result = box.Text.Trim(); w.DialogResult = true; }, "GoldButton");
        ok.IsDefault = true;
        sp.Children.Add(Buttons(ok, K.Btn("Отмена", () => w.DialogResult = false)));
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true && !string.IsNullOrWhiteSpace(result) ? result : null;
    }

    /// <summary>Предупреждение перед бенчмарком. Можно ответить голосом («да» / «нет»).</summary>
    public static (bool Start, bool AllowMic) BenchmarkWarning(JarvisController c)
    {
        var (w, sp) = Frame("Бенчмарк JARVIS", 520);
        sp.Children.Add(K.Text(PerformanceSettingsViewModel.BenchmarkWarning, 14));
        sp.Children.Add(K.Hint("Проверяются: сведения о системе, нагрузка самого JARVIS, голосовой движок на тестовом сигнале, OCR на " +
                               "тестовом изображении, UI Automation на собственном тестовом окне, выполнение шаблонов в песочнице и скорость диска. " +
                               "Чужие программы, файлы, мышь и клавиатура не затрагиваются. Настройки не меняются."));
        var mic = new CheckBox { Content = "Также кратко проверить доступность микрофона (без записи)", IsChecked = false, Margin = new Thickness(0, 8, 0, 0) };
        sp.Children.Add(mic);
        var start = K.Btn(PerformanceSettingsViewModel.StartButton, () => w.DialogResult = true, "GoldButton");
        var cancel = K.Btn(PerformanceSettingsViewModel.CancelButton, () => w.DialogResult = false);
        cancel.IsCancel = true;
        sp.Children.Add(Buttons(start, cancel));
        c.SetBenchmarkPrompt(ok => w.Dispatcher.BeginInvoke(() => { if (w.IsVisible) w.DialogResult = ok; }));
        try
        {
            var r = w.ShowDialog() == true;
            return (r, mic.IsChecked == true);
        }
        finally { c.SetBenchmarkPrompt(null); }
    }

    /// <summary>Редактор значений профиля. Возвращает новые значения или null.</summary>
    public static (string Name, string? Description, Dictionary<string, double> Values)? EditProfile(PerformanceProfile p, bool nameEditable)
    {
        var (w, sp) = Frame($"Профиль «{p.Name}»", 640);
        var name = K.Box(p.Name);
        name.IsEnabled = nameEditable;
        var desc = K.Box(p.Description, null, double.NaN, multiline: true);
        sp.Children.Add(K.Labeled("Название", name));
        sp.Children.Add(K.Labeled("Описание", desc));
        var editors = new Dictionary<string, Func<double?>>();
        foreach (var group in PerformanceCatalog.All.GroupBy(d => d.Group))
        {
            sp.Children.Add(K.H2(group.Key));
            foreach (var d in group)
            {
                var v = p.Value(d);
                if (d.Kind == ParamKind.Bool)
                {
                    var cb = new CheckBox { Content = d.Label, IsChecked = v >= 0.5, ToolTip = d.Description };
                    sp.Children.Add(cb);
                    sp.Children.Add(K.Hint(d.Description));
                    editors[d.Key] = () => cb.IsChecked == true ? 1 : 0;
                }
                else
                {
                    var tb = K.Box(v.ToString(System.Globalization.CultureInfo.GetCultureInfo("ru-RU")), null, 140);
                    sp.Children.Add(K.Labeled($"{d.Label}{(d.Unit.Length > 0 ? ", " + d.Unit : "")} ({d.Format(d.Min)} … {d.Format(d.Max)})", tb,
                        d.Description + (d.RequiresVoiceRestart ? " Применяется с перезапуском распознавателя." : "")));
                    editors[d.Key] = () =>
                        double.TryParse(tb.Text.Replace('.', ','), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), out var x)
                            ? x : null;
                }
            }
        }
        var err = K.Hint("");
        err.Foreground = K.B("Danger");
        sp.Children.Add(err);
        (string, string?, Dictionary<string, double>)? result = null;
        var save = K.Btn("Сохранить", () =>
        {
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (var d in PerformanceCatalog.All)
            {
                var x = editors[d.Key]();
                if (x is null || !d.IsValid(x.Value)) errors.Add($"«{d.Label}»: допустимо {d.Format(d.Min)} … {d.Format(d.Max)}");
                else values[d.Key] = x.Value;
            }
            if (string.IsNullOrWhiteSpace(name.Text)) errors.Add("Укажите название.");
            if (errors.Count > 0) { err.Text = "Исправьте значения:\n" + string.Join("\n", errors); return; }
            result = (name.Text.Trim(), string.IsNullOrWhiteSpace(desc.Text) ? null : desc.Text.Trim(), values);
            w.DialogResult = true;
        }, "GoldButton");
        sp.Children.Add(Buttons(save, K.Btn("Отмена", () => w.DialogResult = false)));
        return w.ShowDialog() == true ? result : null;
    }

    public static void Compare(IReadOnlyList<PerformanceProfile> profiles, PerformanceProfile? left = null, PerformanceProfile? right = null)
    {
        var (w, sp) = Frame("Сравнение профилей", 760);
        var items = profiles.Select(p => (p, $"{p.Name} — {PerformanceSettingsViewModel.KindRu(p.Kind)}")).ToList();
        var a = left ?? profiles[0];
        var b = right ?? profiles.Skip(1).FirstOrDefault() ?? profiles[0];
        var host = new ContentControl();
        void Refresh()
        {
            var diffs = ProfileComparisonService.Compare(a, b);
            var differ = diffs.Count(d => !d.Equal);
            host.Content = K.Stack(
                K.Text(differ == 0 ? "Профили совпадают по всем параметрам." : $"Различаются параметров: {differ} из {diffs.Count}."),
                Table(["Параметр", a.Name, b.Name, ""], diffs.Select(d => new[]
                    { d.Parameter.Label, d.Parameter.Format(d.Left), d.Parameter.Format(d.Right), d.Equal ? "" : "≠" }), [3, 2, 2, 0.4]));
        }
        sp.Children.Add(K.Row(K.Combo(items, a, x => { a = x; Refresh(); }, 330), new TextBlock { Text = "  и  " }, K.Combo(items, b, x => { b = x; Refresh(); }, 330)));
        sp.Children.Add(host);
        Refresh();
        sp.Children.Add(Buttons(K.Btn("Закрыть", () => w.Close())));
        w.ShowDialog();
    }

    /// <summary>Предпросмотр импортируемого профиля: ошибки, предупреждения и значения до сохранения.</summary>
    public static bool ImportPreview(ProfileValidation v)
    {
        var (w, sp) = Frame("Импорт профиля", 640);
        if (v.Errors.Count > 0)
        {
            var e = K.Text("Профиль не может быть импортирован:\n• " + string.Join("\n• ", v.Errors));
            e.Foreground = K.B("Danger");
            e.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(e);
        }
        if (v.Warnings.Count > 0)
        {
            var t = K.Text("Предупреждения:\n• " + string.Join("\n• ", v.Warnings));
            t.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(t);
        }
        if (v.Profile is { } p)
        {
            sp.Children.Add(K.H2(p.Name));
            if (p.Description is { } d) sp.Children.Add(K.Hint(d));
            sp.Children.Add(Table(["Параметр", "Значение"], PerformanceCatalog.All.Select(x => new[] { x.Label, x.Format(p.Value(x)) }), [3, 2]));
            sp.Children.Add(K.Hint("Параметры безопасности профилем не меняются. Профиль будет сохранён как новый пользовательский и не будет применён автоматически."));
        }
        var import = K.Btn("Импортировать", () => w.DialogResult = true, "GoldButton");
        import.IsEnabled = v.IsValid;
        sp.Children.Add(Buttons(import, K.Btn("Отмена", () => w.DialogResult = false)));
        return w.ShowDialog() == true;
    }

    public static PerformanceProfile? ChooseProfile(string title, IReadOnlyList<PerformanceProfile> profiles)
    {
        var (w, sp) = Frame(title, 520);
        var list = new ListBox { Height = 260 };
        foreach (var p in profiles) list.Items.Add($"{p.Name} — {PerformanceSettingsViewModel.KindRu(p.Kind)}");
        list.SelectedIndex = 0;
        sp.Children.Add(list);
        PerformanceProfile? result = null;
        sp.Children.Add(Buttons(K.Btn("Выбрать", () => { if (list.SelectedIndex >= 0) { result = profiles[list.SelectedIndex]; w.DialogResult = true; } }, "GoldButton"),
            K.Btn("Отмена", () => w.DialogResult = false)));
        return w.ShowDialog() == true ? result : null;
    }
}
