using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.Core.Apps;
using Microsoft.Win32;

namespace Jarvis.App.Views.Pages;

/// <summary>Каталог приложений: названия, произношения, способ запуска, процессы.</summary>
public sealed class AppsPage : UserControl, IPage
{
    private readonly JarvisController _c;
    private readonly ListBox _list = new() { Height = 300 };
    private readonly TextBox _search = K.Box("", width: 300);
    private readonly TextBox _name = K.Box(""), _aliases = K.Box(""), _target = K.Box(""), _args = K.Box(""), _procs = K.Box("");
    private readonly ComboBox _kind;
    private readonly CheckBox _hidden = new() { Content = "Скрыть (не распознавать)" };
    private readonly CheckBox _relaunch = new() { Content = "Повторный запуск показывает окно (как Telegram)" };
    private readonly TextBlock _status = K.Hint("");
    private AppEntry? _current;
    private LaunchKind _kindValue;

    public AppsPage(JarvisController c)
    {
        _c = c;
        _kind = K.Combo(new (LaunchKind, string)[]
        {
            (LaunchKind.Executable, "Программа (.exe)"), (LaunchKind.Shortcut, "Ярлык (.lnk)"),
            (LaunchKind.AppUserModelId, "Приложение Microsoft Store (AUMID)"), (LaunchKind.Uri, "Адрес (URI)"),
            (LaunchKind.DefaultBrowser, "Браузер по умолчанию"),
        }, LaunchKind.Executable, k => _kindValue = k);
        _search.TextChanged += (_, _) => Refresh();
        _list.SelectionChanged += (_, _) => Load((_list.SelectedItem as ListBoxItem)?.Tag as AppEntry);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        var left = K.Stack(K.Row(new TextBlock { Text = "Поиск: ", VerticalAlignment = VerticalAlignment.Center }, _search), _list,
            K.Row(K.Btn("Найти программы заново", Rescan, "GoldButton"), K.Btn("Добавить вручную", AddNew)), _status);
        var right = K.Card(
            K.H2("Свойства"),
            K.Labeled("Название", _name),
            K.Labeled("Как произносить (через запятую)", _aliases, "Например: «телега, телеграм». Русские варианты распознаются лучше."),
            K.Labeled("Способ запуска", _kind),
            K.Labeled("Путь / AUMID / адрес", _target),
            K.Row(K.Btn("Обзор…", Browse)),
            K.Labeled("Аргументы", _args),
            K.Labeled("Имена процессов (через запятую)", _procs, "По ним JARVIS находит уже открытое окно, а не запускает копию."),
            _relaunch, _hidden,
            K.Row(K.Btn("Сохранить", Save, "GoldButton"), K.Btn("Проверить запуск", TestLaunch), K.Btn("Удалить", Remove, "DangerButton")));
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Content = K.Page(K.H1("Приложения"), K.Hint("Список собран из меню «Пуск», реестра App Paths и Microsoft Store. Ваши правки сохраняются при повторном поиске."), grid);
        _c.Catalog.Changed += () => Dispatcher.BeginInvoke(Refresh);
    }

    public void OnShown() => Refresh();

    private void Refresh()
    {
        var q = _search.Text.Trim();
        _list.Items.Clear();
        foreach (var a in _c.Catalog.AllIncludingHidden.Where(a => q.Length == 0 || a.AllNames().Any(n => n.Contains(q, StringComparison.CurrentCultureIgnoreCase))).OrderBy(a => a.DisplayName))
            _list.Items.Add(new ListBoxItem { Content = $"{(a.Hidden ? "🚫 " : "")}{a.DisplayName}   ({a.Source})", Tag = a });
        _status.Text = $"Всего: {_c.Catalog.AllIncludingHidden.Count}. Последний поиск: {_c.Catalog.LastScan?.ToString("g") ?? "ещё не выполнялся"}";
    }

    private void Load(AppEntry? a)
    {
        _current = a;
        if (a is null) return;
        _name.Text = a.DisplayName;
        _aliases.Text = string.Join(", ", a.Aliases);
        _kindValue = a.LaunchKind;
        _kind.SelectedIndex = (int)a.LaunchKind;
        _target.Text = a.LaunchTarget ?? "";
        _args.Text = a.Arguments ?? "";
        _procs.Text = string.Join(", ", a.ProcessNames);
        _hidden.IsChecked = a.Hidden;
        _relaunch.IsChecked = a.RelaunchShowsWindow;
    }

    private static List<string> Split(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    private void Save()
    {
        var a = _current ?? new AppEntry { Source = AppSource.User };
        a.DisplayName = _name.Text.Trim();
        if (a.DisplayName.Length == 0) { K.Info("Укажите название"); return; }
        if (string.IsNullOrEmpty(a.Id)) a.Id = AppCatalog.MakeId(a.DisplayName);
        a.Aliases = Split(_aliases.Text);
        a.LaunchKind = _kindValue;
        a.LaunchTarget = string.IsNullOrWhiteSpace(_target.Text) ? null : _target.Text.Trim();
        a.Arguments = string.IsNullOrWhiteSpace(_args.Text) ? null : _args.Text.Trim();
        a.ProcessNames = Split(_procs.Text).Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p).ToList();
        a.Hidden = _hidden.IsChecked == true;
        a.RelaunchShowsWindow = _relaunch.IsChecked == true;
        a.UserEdited = true;
        _c.Catalog.Upsert(a);
        _current = a;
    }

    private void AddNew()
    {
        _current = null;
        _list.SelectedItem = null;
        _name.Text = _aliases.Text = _target.Text = _args.Text = _procs.Text = "";
        _kind.SelectedIndex = 0;
        _hidden.IsChecked = _relaunch.IsChecked = false;
    }

    private void Browse()
    {
        var dlg = new OpenFileDialog { Filter = "Программы и ярлыки (*.exe;*.lnk)|*.exe;*.lnk" };
        if (dlg.ShowDialog() != true) return;
        _target.Text = dlg.FileName;
        _kind.SelectedIndex = dlg.FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (_name.Text.Length == 0) _name.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        if (_procs.Text.Length == 0 && dlg.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            _procs.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    private void TestLaunch()
    {
        if (_current is null) return;
        var app = _current;
        _ = Task.Run(async () =>
        {
            var r = await _c.Runner.Launcher.LaunchAsync(app, false, _c.Stop.BeginRun());
            _c.Reply(r.Message, r.Success ? Core.Abstractions.NotifyLevel.Success : Core.Abstractions.NotifyLevel.Error, speak: false);
        });
    }

    private void Remove()
    {
        if (_current is null) return;
        if (K.Ask($"Удалить «{_current.DisplayName}» из каталога? (Программа на компьютере не затрагивается.)")) _c.Catalog.Remove(_current.Id);
    }

    private async void Rescan()
    {
        _status.Text = "Поиск…";
        try
        {
            var added = await _c.RescanAppsAsync();
            _status.Text = $"Готово, добавлено: {added}";
        }
        catch (Exception ex) { _status.Text = $"Ошибка: {ex.Message}"; }
    }
}
