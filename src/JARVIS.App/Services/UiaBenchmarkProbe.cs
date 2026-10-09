using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Jarvis.Core.Performance;
using Jarvis.Core.Vision;
using Jarvis.Platform;
using Jarvis.Vision;

namespace Jarvis.App.Services;

/// <summary>
/// Замер UI Automation на собственном тестовом окне JARVIS: поиск окон, обход дерева, поиск
/// кнопки по имени, повторная проверка после изменения интерфейса и нажатие через InvokePattern.
/// Чужие программы не трогаются, мышь и клавиатура не используются.
/// </summary>
public sealed class UiaBenchmarkProbe(WindowService windows, VisionService vision) : IBenchmarkProbe
{
    private const string G = "UI Automation";
    public string Title => "Распознавание интерфейса (UI Automation)";
    public BenchmarkStage Stage => BenchmarkStage.Measuring;

    public async Task<IReadOnlyList<Measurement>> RunAsync(BenchmarkContext ctx, CancellationToken ct)
    {
        var list = new List<Measurement>();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            foreach (var (k, t) in Keys) list.Add(Measurement.Unavailable(k, G, t, "Нет графического интерфейса."));
            return list;
        }

        Window? win = null;
        Button? target = null;
        var clicked = 0;
        nint hwnd = 0;
        await dispatcher.InvokeAsync(() =>
        {
            var panel = new WrapPanel { Margin = new Thickness(8) };
            for (var i = 1; i <= 40; i++) panel.Children.Add(new Button { Content = $"Кнопка {i}", Margin = new Thickness(2) });
            target = new Button { Content = "Отправить отчёт", Margin = new Thickness(2) };
            target.Click += (_, _) => Interlocked.Increment(ref clicked);
            panel.Children.Add(target);
            panel.Children.Add(new TextBox { Text = "Тестовое поле", Width = 160 });
            win = new Window
            {
                Title = "JARVIS — тест UI Automation", Width = 520, Height = 360, Content = panel,
                ShowActivated = false, ShowInTaskbar = false, Topmost = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40,
            };
            win.Show();
            hwnd = new WindowInteropHelper(win).Handle;
        });
        try
        {
            await Task.Delay(300, ct);

            var sw = Stopwatch.StartNew();
            windows.Invalidate();
            var wins = windows.GetTopLevelWindows();
            list.Add(new Measurement(MKeys.UiaWindowsMs, G, "Получение списка окон", sw.Elapsed.TotalMilliseconds, "мс", $"{wins.Count} окон"));

            sw.Restart();
            var els = await vision.SnapshotElementsAsync(hwnd, ctx.CurrentParameters.TryGetValue("ui.uiaMaxElements", out var mx) ? (int)mx : 4000, ct);
            list.Add(new Measurement(MKeys.UiaSnapshotMs, G, "Обход дерева элементов тестового окна", sw.Elapsed.TotalMilliseconds, "мс", $"{els.Count} элементов"));

            sw.Restart();
            var found = await vision.FindTextAsync("Отправить отчёт", hwnd, allowOcr: false, ct);
            list.Add(found.Found
                ? new Measurement(MKeys.UiaFindMs, G, "Поиск кнопки по названию", sw.Elapsed.TotalMilliseconds, "мс")
                : Measurement.Unavailable(MKeys.UiaFindMs, G, "Поиск кнопки по названию", found.Detail ?? "Кнопка не найдена."));

            await dispatcher.InvokeAsync(() => target!.Content = "Отправить отчёт повторно");
            await Task.Delay(100, ct);
            sw.Restart();
            var again = await vision.FindTextAsync("Отправить отчёт повторно", hwnd, allowOcr: false, ct);
            list.Add(again.Found
                ? new Measurement(MKeys.UiaRecheckMs, G, "Повторная проверка после изменения интерфейса", sw.Elapsed.TotalMilliseconds, "мс")
                : Measurement.Unavailable(MKeys.UiaRecheckMs, G, "Повторная проверка после изменения интерфейса", "Изменённая кнопка не найдена."));

            if (again.Found && again.NativeElement is AutomationElement el && el.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
            {
                sw.Restart();
                ((InvokePattern)p).Invoke();
                for (var i = 0; i < 20 && Volatile.Read(ref clicked) == 0; i++) await Task.Delay(25, ct);
                list.Add(new Measurement(MKeys.UiaInvoke, G, "Нажатие тестовой кнопки (InvokePattern)", sw.Elapsed.TotalMilliseconds, "мс",
                    Volatile.Read(ref clicked) > 0 ? "сработало" : "нажатие не дошло"));
            }
            else list.Add(Measurement.Unavailable(MKeys.UiaInvoke, G, "Нажатие тестовой кнопки (InvokePattern)", "Шаблон Invoke недоступен."));

            sw.Restart();
            var b = again.Found ? again.Bounds : found.Bounds;
            var under = vision.GetElementAt(b.Center);
            list.Add(under is not null
                ? new Measurement(MKeys.UiaReadMs, G, "Чтение элемента в точке экрана", sw.Elapsed.TotalMilliseconds, "мс")
                : Measurement.Unavailable(MKeys.UiaReadMs, G, "Чтение элемента в точке экрана", "Тестовое окно перекрыто другим окном."));
        }
        finally
        {
            await dispatcher.InvokeAsync(() => win?.Close());
            windows.Invalidate();
        }
        return list;
    }

    private static readonly (string, string)[] Keys =
    [
        (MKeys.UiaWindowsMs, "Получение списка окон"), (MKeys.UiaSnapshotMs, "Обход дерева элементов тестового окна"),
        (MKeys.UiaFindMs, "Поиск кнопки по названию"), (MKeys.UiaRecheckMs, "Повторная проверка после изменения интерфейса"),
        (MKeys.UiaInvoke, "Нажатие тестовой кнопки (InvokePattern)"), (MKeys.UiaReadMs, "Чтение элемента в точке экрана"),
    ];
}
