using System.Diagnostics;
using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.Platform;

namespace Jarvis.App.Views.Pages;

public sealed class AboutPage : UserControl, IPage
{
    private readonly JarvisController _c;

    public AboutPage(JarvisController c)
    {
        _c = c;
        var s = _c.Settings.Current;
        var exe = Environment.ProcessPath ?? "";
        Content = K.Page(
            K.H1("О программе"),
            K.Card(
                K.Text($"JARVIS Desktop Operator {typeof(AboutPage).Assembly.GetName().Version}", 16),
                K.Hint("Локальный голосовой оператор рабочего стола Windows. Работает без нейросетей, облака и телеметрии."),
                K.Text("Технологии: .NET 10 / WPF; распознавание — CMU PocketSphinx (HMM/GMM) или Windows SAPI; синтез — Windows SAPI; " +
                       "окна и ввод — Win32 API; элементы интерфейса — UI Automation; текст на экране — Tesseract (классический режим, без LSTM); звук — NAudio."),
                K.Hint("Лицензии сторонних компонентов: THIRD-PARTY-NOTICES.md в папке программы.")),
            K.Card(K.H2("Запуск"),
                K.Check("Запускать вместе с Windows", AutostartService.IsEnabled(), v =>
                {
                    _c.Settings.Update(x => x.Autostart = v);
                    try { AutostartService.Set(v, exe); } catch (Exception ex) { K.Info($"Не удалось изменить автозапуск: {ex.Message}"); }
                }),
                K.Check("Запускаться свёрнутым в трей", s.StartMinimizedToTray, v => _c.Settings.Update(x => x.StartMinimizedToTray = v)),
                K.Check("Режим отладки (подробный журнал в файл logs)", s.DebugMode, v => _c.Settings.Update(x => x.DebugMode = v))),
            K.Card(K.H2("Данные"),
                K.Text($"Папка данных: {_c.Paths.Root}"),
                K.Hint("settings.json — настройки, apps.json — приложения, anchors.json — запомненные элементы, templates — шаблоны, models и tessdata — модели."),
                K.Row(K.Btn("Открыть папку данных", () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_c.Paths.Root}\"") { UseShellExecute = true })),
                      K.Btn("Сбросить настройки", Reset, "DangerButton"))));
    }

    public void OnShown() { }

    private void Reset()
    {
        if (!K.Ask("Сбросить настройки к значениям по умолчанию? Шаблоны, приложения и привязки сохранятся.")) return;
        _c.Settings.Update(x =>
        {
            var d = new Core.Settings.JarvisSettings();
            foreach (var prop in typeof(Core.Settings.JarvisSettings).GetProperties().Where(p => p.CanWrite))
                prop.SetValue(x, prop.GetValue(d));
        });
        K.Info("Настройки сброшены. Перезапустите JARVIS, чтобы обновить все страницы.");
    }
}
