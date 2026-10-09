using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jarvis.App.Services;
using Jarvis.App.Views.Pages;

namespace Jarvis.App.Views;

public partial class MainWindow : Window
{
    private readonly JarvisController _c;
    private readonly Dictionary<string, UserControl> _pages = new();
    public bool AllowClose { get; set; }

    public MainWindow(JarvisController controller)
    {
        _c = controller;
        InitializeComponent();
        _c.StateChanged += s => Dispatcher.BeginInvoke(() => ShowState(s));
        ShowState(_c.State);
        Nav.SelectedIndex = 0;
    }

    private void ShowState(HudState s)
    {
        StateLabel.Text = s.ToRussian();
        StateDot.Fill = s switch
        {
            HudState.Error => (Brush)FindResource("Danger"),
            HudState.AwaitingConfirmation or HudState.NeedsHelp or HudState.Paused => (Brush)FindResource("Warn"),
            HudState.Disabled => Brushes.Gray,
            HudState.Executing => (Brush)FindResource("Ok"),
            _ => (Brush)FindResource("Accent"),
        };
    }

    public void Navigate(string tag)
    {
        foreach (ListBoxItem item in Nav.Items)
            if ((string)item.Tag == tag) { Nav.SelectedItem = item; return; }
    }

    public void ShowBenchmarkPrompt(Jarvis.Core.Performance.BenchmarkTrigger trigger)
    {
        Navigate("performance");
        if (_pages.TryGetValue("performance", out var p) && p is PerformancePage perf) perf.RequestBenchmark(trigger);
    }

    public void OpenTemplateInEditor(Guid id)
    {
        Navigate("editor");
        if (_pages.TryGetValue("editor", out var p) && p is TemplateEditorPage ed) ed.Select(id);
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem item) return;
        var tag = (string)item.Tag;
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "home" => new HomePage(_c),
                "editor" => new TemplateEditorPage(_c),
                "library" => new LibraryPage(_c, this),
                "apps" => new AppsPage(_c),
                "anchors" => new AnchorsPage(_c),
                "voice" => new VoicePage(_c),
                "security" => new SecurityPage(_c),
                "performance" => new PerformancePage(_c),
                _ => new AboutPage(_c),
            };
            _pages[tag] = page;
        }
        if (page is IPage p) p.OnShown();
        Host.Content = page;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Закрытие окна сворачивает JARVIS в трей; выход — через меню трея.
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}

public interface IPage
{
    void OnShown();
}
