using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Jarvis.App.Services;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Settings;

namespace Jarvis.App.Views;

/// <summary>Компактный индикатор состояния поверх окон. Не перехватывает фокус.</summary>
public partial class HudWindow : Window
{
    private bool _animations = true;
    private DateTime _lineUntil = DateTime.MinValue;
    private readonly System.Windows.Threading.DispatcherTimer _clear;

    public HudWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLong(h, -20);
            SetWindowLong(h, -20, ex | 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/ | 0x20 /*TRANSPARENT: клики проходят насквозь*/);
        };
        _clear = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clear.Tick += (_, _) => { if (DateTime.Now > _lineUntil && LineText.Text.Length > 0 && _lineUntil != DateTime.MinValue) { LineText.Text = ""; StepTextBlock.Text = ""; _lineUntil = DateTime.MinValue; } };
        _clear.Start();
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(nint h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint h, int i, int v);

    public void Apply(JarvisSettings s)
    {
        _animations = s.HudAnimations;
        Opacity = Math.Clamp(s.HudOpacity, 0.3, 1);
        Glow.Opacity = _animations ? 0.7 : 0;
        Place(s.HudCorner);
        if (!_animations) StopAnim();
    }

    private void Place(HudCorner corner)
    {
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        const double m = 16;
        var h = ActualHeight > 0 ? ActualHeight : 110;
        (Left, Top) = corner switch
        {
            HudCorner.TopLeft => (wa.Left + m, wa.Top + m),
            HudCorner.BottomLeft => (wa.Left + m, wa.Bottom - h - m),
            HudCorner.BottomRight => (wa.Right - Width - m, wa.Bottom - h - m),
            _ => (wa.Right - Width - m, wa.Top + m),
        };
    }

    public void SetState(HudState state)
    {
        StateText.Text = state.ToRussian();
        var color = state switch
        {
            HudState.Error => Color.FromRgb(0xEF, 0x44, 0x44),
            HudState.AwaitingConfirmation or HudState.NeedsHelp => Color.FromRgb(0xF5, 0x9E, 0x0B),
            HudState.Paused => Color.FromRgb(0xF5, 0xC0, 0x4A),
            HudState.Disabled => Color.FromRgb(0x64, 0x74, 0x8B),
            HudState.Executing => Color.FromRgb(0x34, 0xD3, 0x99),
            HudState.Listening or HudState.Recognizing => Color.FromRgb(0x38, 0xBD, 0xF8),
            _ => Color.FromRgb(0x22, 0xD3, 0xEE),
        };
        var brush = new SolidColorBrush(color);
        StateText.Foreground = brush;
        Core.Fill = brush;
        OuterRing.Stroke = brush;
        Frame.BorderBrush = brush;
        Glow.Color = color;
        if (_animations && state is HudState.Listening or HudState.Recognizing or HudState.Executing or HudState.AwaitingConfirmation or HudState.NeedsHelp)
            StartAnim(state is HudState.Recognizing or HudState.Executing ? 0.45 : 0.9);
        else StopAnim();
        if (state == HudState.Listening) ShowLine("Слушаю команду…", 6);
    }

    private void StartAnim(double seconds)
    {
        var a = new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        OuterRing.BeginAnimation(OpacityProperty, a);
        Core.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.6, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopAnim()
    {
        OuterRing.BeginAnimation(OpacityProperty, null);
        Core.BeginAnimation(OpacityProperty, null);
        OuterRing.Opacity = 1;
        Core.Opacity = 1;
    }

    public void ShowLine(string text, double seconds = 5, NotifyLevel level = NotifyLevel.Info)
    {
        LineText.Text = text;
        LineText.Foreground = level switch
        {
            NotifyLevel.Error => new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)),
            NotifyLevel.Warning => new SolidColorBrush(Color.FromRgb(0xFD, 0xE6, 0x8A)),
            NotifyLevel.Success => new SolidColorBrush(Color.FromRgb(0xA7, 0xF3, 0xD0)),
            _ => (Brush)FindResource("Fg"),
        };
        _lineUntil = DateTime.Now.AddSeconds(seconds);
    }

    public void ShowStep(string text)
    {
        StepTextBlock.Text = text;
        _lineUntil = DateTime.Now.AddSeconds(8);
    }

    public void SetMic(double db, bool muted, bool active)
    {
        if (muted || !active)
        {
            MicBar.Width = 0;
            MicText.Text = muted ? "Микрофон выключен" : "Микрофон не активен";
            return;
        }
        var frac = Math.Clamp((db + 70) / 60, 0, 1);
        MicBar.Width = frac * 240;
        MicText.Text = $"Микрофон: {db:F0} дБ";
    }
}
