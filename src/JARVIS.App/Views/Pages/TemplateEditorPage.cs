using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Jarvis.App.Services;
using Jarvis.Core.Security;
using Jarvis.Templates;

namespace Jarvis.App.Views.Pages;

/// <summary>
/// Визуальный редактор шаблонов: блоки в дереве, перетаскивание, параметры,
/// условия «если/иначе», повторы, вложенные шаблоны, проверка, тестовый запуск, импорт/экспорт.
/// </summary>
public sealed partial class TemplateEditorPage : UserControl, IPage
{
    private readonly JarvisController _c;
    private readonly ListBox _templates = new() { MinHeight = 380 };
    private readonly TreeView _tree = new() { MinHeight = 380, AllowDrop = true };
    private readonly StackPanel _props = new();
    private readonly TextBox _name = K.Box(""), _desc = K.Box(""), _phrases = K.Box("");
    private readonly CheckBox _enabled = new() { Content = "Шаблон включён" };
    private readonly ComboBox _palette = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _issues = K.Hint("");
    private Template? _draft;
    private Guid? _selectedStep;
    private (TemplateStep Parent, string Branch)? _selectedBranch;
    private bool _dirty;
    private Point _dragStart;

    private sealed record BranchTag(TemplateStep Parent, string Branch);

    public TemplateEditorPage(JarvisController c)
    {
        _c = c;
        foreach (var d in StepCatalog.All)
            _palette.Items.Add(new ComboBoxItem { Content = $"{d.Group}: {d.Title}", Tag = d.Kind, ToolTip = d.Description });
        _palette.SelectedIndex = 0;
        _templates.SelectionChanged += (_, _) =>
        {
            if (_templates.SelectedItem is ListBoxItem { Tag: Template t } && t.Id != _draft?.Id) Open(t);
        };
        _tree.SelectedItemChanged += (_, _) => OnTreeSelection();
        _tree.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(null);
        _tree.PreviewMouseMove += TreeMouseMove;
        _tree.Drop += TreeDrop;
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Delete) DeleteStep(); };
        foreach (var tb in new[] { _name, _desc, _phrases }) tb.TextChanged += (_, _) => _dirty = true;
        _enabled.Click += (_, _) => _dirty = true;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = K.Stack(K.H2("Шаблоны"), _templates,
            K.Row(K.Btn("Новый", NewTemplate, "GoldButton"), K.Btn("Копия", DuplicateTemplate), K.Btn("Удалить", DeleteTemplate, "DangerButton")),
            K.Row(K.Btn("Импорт…", Import), K.Btn("Экспорт…", Export)));
        var middle = K.Stack(
            K.H2("Шаблон"),
            K.Labeled("Название", _name),
            K.Labeled("Голосовые фразы запуска (через запятую)", _phrases, "Например: «рабочий режим». Скажите: «Джарвис, рабочий режим»."),
            K.Labeled("Описание", _desc),
            _enabled,
            K.H2("Блоки (можно перетаскивать мышью)"),
            K.Row(_palette, K.Btn("+ Добавить", AddStep, "GoldButton")),
            _tree,
            K.Row(K.Btn("↑", () => MoveStep(-1), tip: "Выше"), K.Btn("↓", () => MoveStep(1), tip: "Ниже"),
                  K.Btn("Дублировать", DuplicateStep), K.Btn("Вкл/выкл блок", ToggleStep), K.Btn("Удалить блок", DeleteStep, "DangerButton")),
            K.Row(K.Btn("💾 Сохранить", Save, "GoldButton"), K.Btn("Проверить", () => Validate()), K.Btn("▶ Тестовый запуск", TestRun),
                  K.Btn("■ Стоп", () => _c.StopEverything("Остановлено"), "DangerButton")),
            _issues);
        var right = K.Stack(K.H2("Свойства блока"), new Border { Style = K.S("Card"), Child = _props });
        Place(grid, left, 0); Place(grid, middle, 2); Place(grid, right, 4);
        Content = new ScrollViewer { Content = K.Stack(K.H1("Редактор шаблонов"), grid) };
        _c.Templates.Changed += () => Dispatcher.BeginInvoke(RefreshList);
    }

    private static void Place(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

    public void OnShown()
    {
        RefreshList();
        if (_draft is null && _c.Templates.All.Count > 0) Open(_c.Templates.All.OrderBy(t => t.Name).First());
        if (_draft is null) ShowProps();
    }

    public void Select(Guid id)
    {
        RefreshList();
        if (_c.Templates.Get(id) is { } t) Open(t);
    }

    private void RefreshList()
    {
        _templates.Items.Clear();
        foreach (var t in _c.Templates.All.OrderBy(t => t.Name))
        {
            var item = new ListBoxItem { Content = (t.Enabled ? "" : "⏸ ") + t.Name, Tag = t };
            _templates.Items.Add(item);
            if (t.Id == _draft?.Id) _templates.SelectedItem = item;
        }
    }

    private bool ConfirmDiscard() => !_dirty || K.Ask("Есть несохранённые изменения. Отказаться от них?");

    private void Open(Template t)
    {
        if (_draft is not null && _draft.Id != t.Id && !ConfirmDiscard()) { RefreshList(); return; }
        _draft = Jarvis.Core.Storage.JsonStore.DeepClone(t)!;
        _name.Text = _draft.Name;
        _desc.Text = _draft.Description ?? "";
        _phrases.Text = string.Join(", ", _draft.VoicePhrases);
        _enabled.IsChecked = _draft.Enabled;
        _selectedStep = null;
        _selectedBranch = null;
        _issues.Text = "";
        RebuildTree();
        ShowProps();
        _dirty = false;
    }

    // ───────────── дерево ─────────────

    private void RebuildTree()
    {
        _tree.Items.Clear();
        if (_draft is null) return;
        foreach (var s in _draft.Steps) _tree.Items.Add(MakeItem(s));
        if (_draft.Steps.Count == 0)
            _tree.Items.Add(new TreeViewItem { Header = "Пусто. Выберите блок и нажмите «+ Добавить».", Foreground = K.B("Muted"), IsEnabled = false });
    }

    private string Header(TemplateStep s)
    {
        var danger = StepCatalog.GetDangerCategory(s, _c.Security);
        return (s.Enabled ? "" : "⏸ ") + StepCatalog.Describe(s) + (danger != DangerCategory.None ? "  ⚠" : "") +
               (string.IsNullOrWhiteSpace(s.Comment) ? "" : $"   // {s.Comment}");
    }

    private TreeViewItem MakeItem(TemplateStep s)
    {
        var desc = StepCatalog.Get(s.Kind);
        var danger = StepCatalog.GetDangerCategory(s, _c.Security);
        var header = new TextBlock
        {
            Text = Header(s),
            Foreground = !s.Enabled ? K.B("Muted") : danger != DangerCategory.None ? K.B("Warn") : desc.IsContainer ? K.B("Gold") : K.B("Fg"),
            ToolTip = desc.Description,
        };
        var item = new TreeViewItem { Header = header, Tag = s, IsExpanded = true, IsSelected = s.Id == _selectedStep };
        if (s.Kind == StepKind.If)
        {
            item.Items.Add(MakeBranch(s, nameof(TemplateStep.Then), "То (если условие выполнено)", s.Then));
            item.Items.Add(MakeBranch(s, nameof(TemplateStep.Else), "Иначе", s.Else));
        }
        else if (s.Kind == StepKind.Repeat)
            item.Items.Add(MakeBranch(s, nameof(TemplateStep.Body), "Повторяемые блоки", s.Body));
        return item;
    }

    private TreeViewItem MakeBranch(TemplateStep parent, string branch, string title, List<TemplateStep> steps)
    {
        var bi = new TreeViewItem
        {
            Header = new TextBlock { Text = $"▸ {title}", Foreground = K.B("Accent"), FontStyle = FontStyles.Italic },
            Tag = new BranchTag(parent, branch),
            IsExpanded = true,
            IsSelected = _selectedBranch is { } sb && sb.Parent.Id == parent.Id && sb.Branch == branch,
        };
        foreach (var s in steps) bi.Items.Add(MakeItem(s));
        return bi;
    }

    private void OnTreeSelection()
    {
        switch ((_tree.SelectedItem as TreeViewItem)?.Tag)
        {
            case TemplateStep s: _selectedStep = s.Id; _selectedBranch = null; break;
            case BranchTag b: _selectedBranch = (b.Parent, b.Branch); _selectedStep = null; break;
            default: _selectedStep = null; _selectedBranch = null; break;
        }
        ShowProps();
    }

    private TemplateStep? SelectedStep => _draft is null || _selectedStep is null ? null : _draft.AllSteps().FirstOrDefault(s => s.Id == _selectedStep);

    private static List<TemplateStep> Branch(TemplateStep parent, string branch) => branch switch
    {
        nameof(TemplateStep.Then) => parent.Then,
        nameof(TemplateStep.Else) => parent.Else,
        _ => parent.Body,
    };

    /// <summary>Находит список, в котором лежит шаг.</summary>
    internal static List<TemplateStep>? FindList(List<TemplateStep> list, Guid id)
    {
        if (list.Any(s => s.Id == id)) return list;
        foreach (var s in list)
            foreach (var child in new[] { s.Then, s.Else, s.Body })
                if (FindList(child, id) is { } found) return found;
        return null;
    }

    private void Changed()
    {
        _dirty = true;
        RebuildTree();
        ShowProps();
    }

    // ───────────── операции с блоками ─────────────

    private void AddStep()
    {
        if (_draft is null) NewTemplate();
        if (_draft is null || _palette.SelectedItem is not ComboBoxItem { Tag: StepKind kind }) return;
        var step = StepCatalog.Create(kind);
        if (_selectedBranch is { } b) Branch(b.Parent, b.Branch).Add(step);
        else if (SelectedStep is { } sel && FindList(_draft.Steps, sel.Id) is { } list) list.Insert(list.IndexOf(sel) + 1, step);
        else _draft.Steps.Add(step);
        _selectedStep = step.Id;
        _selectedBranch = null;
        Changed();
    }

    private void MoveStep(int delta)
    {
        if (_draft is null || SelectedStep is not { } s || FindList(_draft.Steps, s.Id) is not { } list) return;
        var i = list.IndexOf(s);
        var j = i + delta;
        if (j < 0 || j >= list.Count) return;
        list.RemoveAt(i);
        list.Insert(j, s);
        Changed();
    }

    private void DuplicateStep()
    {
        if (_draft is null || SelectedStep is not { } s || FindList(_draft.Steps, s.Id) is not { } list) return;
        var copy = s.DeepCopy();
        list.Insert(list.IndexOf(s) + 1, copy);
        _selectedStep = copy.Id;
        Changed();
    }

    private void ToggleStep()
    {
        if (SelectedStep is not { } s) return;
        s.Enabled = !s.Enabled;
        Changed();
    }

    private void DeleteStep()
    {
        if (_draft is null || SelectedStep is not { } s || FindList(_draft.Steps, s.Id) is not { } list) return;
        var nested = s.Then.Count + s.Else.Count + s.Body.Count;
        if (nested > 0 && !K.Ask($"Удалить блок вместе с вложенными ({nested})?")) return;
        var i = list.IndexOf(s);
        list.Remove(s);
        _selectedStep = list.Count == 0 ? null : list[Math.Min(i, list.Count - 1)].Id;
        Changed();
    }

    // ───────────── перетаскивание ─────────────

    private void TreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (FindItem(e.OriginalSource as DependencyObject)?.Tag is TemplateStep s)
            DragDrop.DoDragDrop(_tree, new DataObject("jarvis-step", s.Id), DragDropEffects.Move);
    }

    private static bool Inside(TemplateStep container, object? t) => t switch
    {
        TemplateStep ts => ts.Id == container.Id || container.Then.Concat(container.Else).Concat(container.Body).Any(c => Inside(c, ts)),
        BranchTag bt => Inside(container, bt.Parent),
        _ => false,
    };

    private void TreeDrop(object sender, DragEventArgs e)
    {
        if (_draft is null || e.Data.GetData("jarvis-step") is not Guid id) return;
        var moving = _draft.AllSteps().FirstOrDefault(s => s.Id == id);
        if (moving is null) return;
        var target = FindItem(e.OriginalSource as DependencyObject)?.Tag;
        if (target is not null && Inside(moving, target)) return; // нельзя вложить блок в самого себя
        var from = FindList(_draft.Steps, id);
        if (from is null) return;
        from.Remove(moving);
        switch (target)
        {
            case BranchTag b: Branch(b.Parent, b.Branch).Add(moving); break;
            case TemplateStep t when FindList(_draft.Steps, t.Id) is { } list: list.Insert(list.IndexOf(t), moving); break;
            default: _draft.Steps.Add(moving); break;
        }
        _selectedStep = moving.Id;
        Changed();
    }

    private static TreeViewItem? FindItem(DependencyObject? d)
    {
        while (d is not null and not TreeViewItem)
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }
}