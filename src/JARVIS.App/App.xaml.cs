using System.IO;
using System.Windows;
using System.Windows.Threading;
using Jarvis.App.Services;
using Jarvis.App.Views;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Settings;
using Jarvis.Platform;
using Forms = System.Windows.Forms;

namespace Jarvis.App;

public partial class App : Application
{
    private const string MutexName = @"Local\JARVIS-Desktop-Operator";
    private const string ShowEventName = @"Local\JARVIS-Desktop-Operator-Show";
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private JarvisController? _c;
    private MainWindow? _main;
    private HudWindow? _hud;
    private Forms.NotifyIcon? _tray;
    private DispatcherTimer? _micTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--selftest"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = await SelfTest.RunAsync(e.Args);
            Shutdown(code);
            return;
        }
        _mutex = new Mutex(true, MutexName, out var isNew);
        if (!isNew)
        {
            // JARVIS уже запущен: просим первую копию показать окно.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() => { while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowMain); }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            _c?.Log.Error($"Необработанная ошибка: {ex.Exception}");
            _c?.Input.ReleaseAll();
            MessageBox.Show($"Ошибка: {ex.Exception.Message}\nJARVIS продолжит работу.", "JARVIS", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { try { _c?.Input.ReleaseAll(); } catch { } };
        TaskScheduler.UnobservedTaskException += (_, ex) => { _c?.Log.Error($"Фоновая ошибка: {ex.Exception.Message}"); ex.SetObserved(); };

        try
        {
            _c = new JarvisController();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запустить JARVIS: {ex.Message}", "JARVIS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ApplyAutostart();
        _main = new MainWindow(_c);
        _hud = new HudWindow();
        _hud.Apply(_c.Settings.Current);
        _hud.Moved += (x, y) => _c.Settings.Update(st => { st.HudCorner = HudCorner.Custom; st.HudLeft = x; st.HudTop = y; });
        _hud.ResetRequested += () => _c.Settings.Update(st => { st.HudCorner = HudCorner.TopRight; st.HudLeft = null; st.HudTop = null; });
        if (_c.Settings.Current.HudEnabled) _hud.Show();
        _c.StateChanged += s => Dispatcher.BeginInvoke(() => { _hud?.SetState(s); UpdateTray(); });
        _c.Message += (m, lvl) => Dispatcher.BeginInvoke(() => _hud?.ShowLine(m, lvl == NotifyLevel.Error ? 10 : 6, lvl));
        _c.Heard += t => Dispatcher.BeginInvoke(() => _hud?.ShowLine($"«{t}»", 6));
        _c.StepText += t => Dispatcher.BeginInvoke(() => _hud?.ShowStep(t));
        _c.Settings.Changed += s => Dispatcher.BeginInvoke(() =>
        {
            if (_hud is null) return;
            _hud.Apply(s);
            if (s.HudEnabled && !_hud.IsVisible) _hud.Show();
            else if (!s.HudEnabled && _hud.IsVisible) _hud.Hide();
            if (_micTimer is not null) _micTimer.Interval = TimeSpan.FromMilliseconds(s.Performance.HudRefreshMs);
        });
        _c.BenchmarkRequested += trigger => Dispatcher.BeginInvoke(() =>
        {
            ShowMain();
            _main?.ShowBenchmarkPrompt(trigger);
        });
        _micTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_c.Settings.Current.Performance.HudRefreshMs) };
        _micTimer.Tick += (_, _) => _hud?.SetMic(_c.MicLevelDb, _c.Settings.Current.MicrophoneMuted, _c.Voice is not null && _c.Audio.IsRunning);
        _micTimer.Start();
        CreateTray();

        var startHidden = e.Args.Contains("--tray") && _c.Settings.Current.StartMinimizedToTray;
        if (!startHidden) ShowMain();
        await _c.StartAsync();
        _hud.SetState(_c.State);
    }

    private void ApplyAutostart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            if (_c!.Settings.Current.Autostart != AutostartService.IsEnabled()) AutostartService.Set(_c.Settings.Current.Autostart, exe);
        }
        catch (Exception ex) { _c?.Log.Warn($"Автозапуск: {ex.Message}"); }
    }

    private void CreateTray()
    {
        System.Drawing.Icon icon;
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/jarvis.ico"));
            icon = res is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(res.Stream);
        }
        catch { icon = System.Drawing.SystemIcons.Application; }
        _tray = new Forms.NotifyIcon { Icon = icon, Text = "JARVIS", Visible = true };
        _tray.DoubleClick += (_, _) => ShowMain();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть JARVIS", null, (_, _) => ShowMain());
        menu.Items.Add("■ Стоп", null, (_, _) => _c?.StopEverything("Остановлено"));
        menu.Items.Add("Пауза / продолжить", null, (_, _) => _c?.TogglePause());
        menu.Items.Add("Отключить / включить голос", null, (_, _) => { if (_c is not null) _c.SetDisabled(!_c.IsDisabled); });
        menu.Items.Add("Показать / скрыть HUD", null, (_, _) => _c?.Settings.Update(s => s.HudEnabled = !s.HudEnabled));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
    }

    private void UpdateTray()
    {
        if (_tray is null || _c is null) return;
        var text = $"JARVIS — {_c.State.ToRussian()}";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void ExitApp()
    {
        try { _c?.Dispose(); } catch { }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        if (_main is not null) { _main.AllowClose = true; _main.Close(); }
        _hud?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _c?.Input.ReleaseAll(); } catch { }
        if (_tray is not null) _tray.Visible = false;
        _mutex?.Dispose();
        base.OnExit(e);
    }
}