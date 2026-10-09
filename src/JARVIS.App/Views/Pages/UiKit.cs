using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Jarvis.App.Views.Pages;

/// <summary>Небольшой конструктор интерфейса для страниц настроек (единый стиль).</summary>
internal static class K
{
    public static Style S(string key) => (Style)Application.Current.Resources[key];
    public static Brush B(string key) => (Brush)Application.Current.Resources[key];

    public static TextBlock H1(string t) => new() { Text = t, Style = S("H1") };
    public static TextBlock H2(string t) => new() { Text = t, Style = S("H2") };
    public static TextBlock Hint(string t) => new() { Text = t, Style = S("Hint") };
    public static TextBlock Text(string t, double size = 13) => new() { Text = t, FontSize = size, Margin = new Thickness(0, 0, 0, 4) };

    public static Border Card(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return new Border { Style = S("Card"), Child = sp };
    }

    public static StackPanel Stack(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static WrapPanel Row(params UIElement[] children)
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var c in children) wp.Children.Add(c);
        return wp;
    }

    public static Button Btn(string text, Action onClick, string? style = null, string? tip = null)
    {
        var b = new Button { Content = text, ToolTip = tip };
        if (style is not null) b.Style = S(style);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static CheckBox Check(string text, bool value, Action<bool> onChange, string? tip = null)
    {
        var c = new CheckBox { Content = text, IsChecked = value, ToolTip = tip };
        c.Checked += (_, _) => onChange(true);
        c.Unchecked += (_, _) => onChange(false);
        return c;
    }

    public static UIElement Labeled(string label, UIElement control, string? hint = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        sp.Children.Add(new Label { Content = label });
        sp.Children.Add(control);
        if (hint is not null) sp.Children.Add(Hint(hint));
        return sp;
    }

    public static TextBox Box(string? value, Action<string>? onLostFocus = null, double width = double.NaN, bool multiline = false)
    {
        var t = new TextBox { Text = value ?? "", Width = width, HorizontalAlignment = double.IsNaN(width) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
        if (multiline)
        {
            t.AcceptsReturn = true;
            t.TextWrapping = TextWrapping.Wrap;
            t.MinHeight = 70;
            t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        if (onLostFocus is not null) t.LostFocus += (_, _) => onLostFocus(t.Text);
        return t;
    }

    public static TextBox Number(double value, Action<double> onChange, double min, double max, double width = 120)
    {
        var t = new TextBox { Text = value.ToString(System.Globalization.CultureInfo.CurrentCulture), Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        t.LostFocus += (_, _) =>
        {
            if (double.TryParse(t.Text.Replace('.', ','), System.Globalization.NumberStyles.Float, new System.Globalization.CultureInfo("ru-RU"), out var v) ||
                double.TryParse(t.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                v = Math.Clamp(v, min, max);
                onChange(v);
                t.Text = v.ToString(System.Globalization.CultureInfo.CurrentCulture);
            }
            else t.Text = value.ToString(System.Globalization.CultureInfo.CurrentCulture);
        };
        return t;
    }

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Label)> items, T selected, Action<T> onChange, double width = 320)
    {
        var list = items.ToList();
        var c = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (_, label) in list) c.Items.Add(label);
        var idx = list.FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected));
        c.SelectedIndex = idx < 0 ? 0 : idx;
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) onChange(list[c.SelectedIndex].Value); };
        return c;
    }

    public static ScrollViewer Page(params UIElement[] children)
    {
        var sp = Stack(children);
        sp.Margin = new Thickness(0, 0, 12, 20);
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public static void Info(string text, string title = "JARVIS") =>
        MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Ask(string text, string title = "JARVIS") =>
        MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
