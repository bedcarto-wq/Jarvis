using System.Windows;
using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.Templates;
using Microsoft.Win32;

namespace Jarvis.App.Views.Pages;

/// <summary>Библиотека шаблонов: поиск, запуск, копирование, экспорт/импорт, удаление.</summary>
public sealed class LibraryPage : UserControl, IPage
{
    private readonly JarvisController _c;
    private readonly MainWindow _main;
    private readonly ListBox _list = new() { Height = 420, DisplayMemberPath = "Title" };
    private readonly TextBox _search = K.Box("", width: 360);

    private sealed record Item(Template T)
    {
        public string Title => $"{(T.Enabled ? "" : "⏸ ")}{T.Name}   —   {T.Steps.Count} шаг(ов)" +
                               (T.VoicePhrases.Count > 0 ? $"   🎙 «{string.Join("», «", T.VoicePhrases)}»" : "");
    }

    public LibraryPage(JarvisController c, MainWindow main)
    {
        _c = c;
        _main = main;
        _search.TextChanged += (_, _) => Refresh();
        _list.MouseDoubleClick += (_, _) => Edit();
        Content = K.Page(
            K.H1("Библиотека шаблонов"),
            K.Hint("Шаблон — сценарий из блоков. Запускается голосом по своей фразе или «Джарвис, запусти шаблон …»."),
            K.Row(new TextBlock { Text = "Поиск: ", VerticalAlignment = VerticalAlignment.Center }, _search),
            _list,
            K.Row(
                K.Btn("▶ Запустить", Run, "GoldButton"),
                K.Btn("Изменить", Edit),
                K.Btn("Копировать", Duplicate),
                K.Btn("Вкл/выкл", Toggle),
                K.Btn("Экспорт…", Export),
                K.Btn("Импорт…", Import),
                K.Btn("Удалить", Delete, "DangerButton")));
        _c.Templates.Changed += () => Dispatcher.BeginInvoke(Refresh);
    }

    public void OnShown() => Refresh();

    private void Refresh()
    {
        var q = _search.Text.Trim();
        var items = _c.Templates.All
            .Where(t => q.Length == 0 || t.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                        t.VoicePhrases.Any(p => p.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(t => t.Name).Select(t => new Item(t)).ToList();
        _list.ItemsSource = items;
    }

    private Template? Selected => (_list.SelectedItem as Item)?.T;

    private void Run()
    {
        if (Selected is not { } t) return;
        _ = Task.Run(async () =>
        {
            var r = await _c.Executor.RunAsync(t);
            _c.Reply(r.Message, r.Status == ExecutionStatus.Completed ? Core.Abstractions.NotifyLevel.Success : Core.Abstractions.NotifyLevel.Warning, speak: false);
        });
    }

    private void Edit() { if (Selected is { } t) _main.OpenTemplateInEditor(t.Id); }

    private void Duplicate() { if (Selected is { } t) _c.Templates.Duplicate(t.Id); }

    private void Toggle()
    {
        if (Selected is not { } t) return;
        t.Enabled = !t.Enabled;
        _c.Templates.Save(t);
    }

    private void Delete()
    {
        if (Selected is not { } t) return;
        if (K.Ask($"Удалить шаблон «{t.Name}»? Его можно будет восстановить только из экспортированного файла.")) _c.Templates.Delete(t.Id);
    }

    private void Export()
    {
        var items = Selected is { } t ? new[] { t } : _c.Templates.All.ToArray();
        var dlg = new SaveFileDialog { Filter = "Шаблоны JARVIS (*.json)|*.json", FileName = items.Length == 1 ? items[0].Name + ".json" : "jarvis-templates.json" };
        if (dlg.ShowDialog() != true) return;
        _c.Templates.Export(items, dlg.FileName);
        K.Info($"Экспортировано шаблонов: {items.Length}");
    }

    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "Шаблоны JARVIS (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var imported = _c.Templates.Import(dlg.FileName);
            K.Info($"Импортировано шаблонов: {imported.Count}. Опасные шаги по-прежнему потребуют подтверждения.");
        }
        catch (Exception ex)
        {
            K.Info($"Не удалось импортировать: {ex.Message}");
        }
    }
}
