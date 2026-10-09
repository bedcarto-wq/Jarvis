using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Jarvis.Core.Abstractions;
using Jarvis.Platform;

namespace Jarvis.App.Views;

internal static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static Window StyledWindow(string title, double width)
    {
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Background = Res("Bg"),
            Foreground = Res("Fg"),
            ShowInTaskbar = true,
        };
    }
}

/// <summary>Подтверждение опасного действия. Можно ответить голосом: «да» / «нет».</summary>
public sealed class ConfirmWindow
{
    private readonly Window _w;
    private bool _answered;
    public event Action<bool>? Answered;

    public ConfirmWindow(string description, string category)
    {
        _w = Ui.StyledWindow("JARVIS — подтверждение", 460);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "⚠ Требуется подтверждение", Style = (Style)Application.Current.Resources["H1"], Foreground = Ui.Res("Warn") });
        panel.Children.Add(new TextBlock { Text = description, FontSize = 15, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = $"Категория: {category}", Style = (Style)Application.Current.Resources["Hint"] });
        panel.Children.Add(new TextBlock { Text = "Ответьте голосом «да» или «нет», либо нажмите кнопку.", Style = (Style)Application.Current.Resources["Hint"] });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var yes = new Button { Content = "Да, выполнить", Style = (Style)Application.Current.Resources["DangerButton"] };
        var no = new Button { Content = "Нет, отменить", IsDefault = true, IsCancel = true };
        yes.Click += (_, _) => Answer(true);
        no.Click += (_, _) => Answer(false);
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        panel.Children.Add(buttons);
        _w.Content = panel;
        _w.Closed += (_, _) => { if (!_answered) { _answered = true; Answered?.Invoke(false); } };
    }

    private void Answer(bool ok)
    {
        if (_answered) return;
        _answered = true;
        Answered?.Invoke(ok);
        _w.Close();
    }

    public void Show() => _w.Show();
    public void Activate() => _w.Activate();
    public void CloseSilently() { _answered = true; if (_w.IsVisible) _w.Close(); }
}

/// <summary>Выбор варианта (например, какое из похожих приложений открыть).</summary>
public sealed class ChooseWindow
{
    private readonly Window _w;
    private bool _done;
    public event Action<int?>? Chosen;

    public ChooseWindow(string question, IReadOnlyList<string> options)
    {
        _w = Ui.StyledWindow("JARVIS — уточнение", 420);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = question, FontSize = 15, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "Скажите номер варианта («один», «два»…) или нажмите кнопку.", Style = (Style)Application.Current.Resources["Hint"] });
        for (var i = 0; i < options.Count; i++)
        {
            var idx = i;
            var b = new Button { Content = $"{i + 1}. {options[i]}", HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Click += (_, _) => Pick(idx);
            panel.Children.Add(b);
        }
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        cancel.Click += (_, _) => Pick(null);
        panel.Children.Add(cancel);
        _w.Content = panel;
        _w.Closed += (_, _) => { if (!_done) { _done = true; Chosen?.Invoke(null); } };
    }

    private void Pick(int? i)
    {
        if (_done) return;
        _done = true;
        Chosen?.Invoke(i);
        _w.Close();
    }

    public void Show() => _w.Show();
    public void Activate() => _w.Activate();
    public void CloseSilently() { _done = true; if (_w.IsVisible) _w.Close(); }
}

/// <summary>
/// Полупрозрачная накладка на все мониторы: пользователь показывает элемент щелчком.
/// Esc — отмена. Можно навести курсор и сказать «здесь».
/// </summary>
public sealed class PointOverlay : Window
{
    public event Action<ScreenPoint>? Picked;
    public event Action? Cancelled;
    private bool _done;

    public PointOverlay(string message)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0x30, 0x22, 0xD3, 0xEE));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x10, 0x1E)),
            BorderBrush = Ui.Res("Accent"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 60, 0, 0),
            Child = new TextBlock
            {
                Text = message + "\nЩёлкните по нужному месту (Esc — отмена) или наведите курсор и скажите «здесь».",
                FontSize = 16,
                Foreground = Ui.Res("Fg"),
                TextAlignment = TextAlignment.Center,
            },
        };
        Content = new Grid { Children = { border } };
        MouseLeftButtonUp += (_, _) =>
        {
            if (_done) return;
            _done = true;
            // Физические экранные координаты берём из системы, а не из WPF (учёт масштабирования).
            Win32Cursor.Get(out var x, out var y);
            Hide();
            Picked?.Invoke(new ScreenPoint(x, y));
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_done) { _done = true; Cancelled?.Invoke(); } };
        Closed += (_, _) => { if (!_done) { _done = true; Cancelled?.Invoke(); } };
    }
}

internal static class Win32Cursor
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);
    private struct POINT { public int X, Y; }
    public static void Get(out int x, out int y) { GetCursorPos(out var p); x = p.X; y = p.Y; }
}
