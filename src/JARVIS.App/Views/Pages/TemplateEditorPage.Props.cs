using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Security;
using Jarvis.Templates;
using Microsoft.Win32;

namespace Jarvis.App.Views.Pages;

public sealed partial class TemplateEditorPage
{
    private void ShowProps()
    {
        _props.Children.Clear();
        if (_draft is null) { _props.Children.Add(K.Hint("Создайте или выберите шаблон.")); return; }
        if (_selectedBranch is { } br)
        {
            _props.Children.Add(K.Hint($"Ветка «{br.Branch}». Новые блоки добавятся сюда. Можно перетащить блок на ветку, чтобы вложить его."));
            return;
        }
        if (SelectedStep is not { } s) { _props.Children.Add(K.Hint("Выберите блок в дереве.")); return; }
        var d = StepCatalog.Get(s.Kind);
        _props.Children.Add(K.Text(d.Title, 15));
        _props.Children.Add(K.Hint(d.Description));
        foreach (var p in d.Parameters) _props.Children.Add(ParamEditor(s.Parameters, p, OnParamChanged));
        if (d.HasCondition)
        {
            if (s.Kind == StepKind.Repeat)
                _props.Children.Add(K.Check("Условие выхода из повтора", s.Condition is not null, v =>
                {
                    s.Condition = v ? new StepCondition { Kind = ConditionKind.WindowExists } : null;
                    Changed();
                }));
            if (s.Condition is not null) ConditionEditor(s.Condition);
        }
        if (!d.IsContainer)
        {
            _props.Children.Add(K.Labeled("Повторов при неудаче (пусто — по умолчанию)", K.Box(s.Retries?.ToString(), v =>
            {
                s.Retries = int.TryParse(v, out var r) ? Math.Clamp(r, 0, _c.Settings.Current.MaxRetriesCap) : null;
                _dirty = true;
            }, 120)));
            _props.Children.Add(K.Check("Продолжать шаблон, если блок не удался", s.ContinueOnError, v => { s.ContinueOnError = v; _dirty = true; }));
        }
        _props.Children.Add(K.Check("Считать опасным (всегда спрашивать подтверждение)", s.MarkedDangerous is not null, v =>
        {
            s.MarkedDangerous = v ? DangerCategory.Custom : null;
            Changed();
        }));
        _props.Children.Add(K.Check("Блок включён", s.Enabled, v => { s.Enabled = v; Changed(); }));
        _props.Children.Add(K.Labeled("Комментарий", K.Box(s.Comment, v => { s.Comment = string.IsNullOrWhiteSpace(v) ? null : v; Changed(); })));
    }

    private void OnParamChanged()
    {
        _dirty = true;
        RefreshHeaders(_tree);
    }

    /// <summary>Обновляет подписи блоков без перестройки дерева (чтобы не терять фокус ввода).</summary>
    private void RefreshHeaders(ItemsControl ic)
    {
        foreach (var o in ic.Items)
            if (o is TreeViewItem tvi)
            {
                if (tvi.Tag is TemplateStep st && tvi.Header is TextBlock tb) tb.Text = Header(st);
                RefreshHeaders(tvi);
            }
    }

    private void ConditionEditor(StepCondition cond)
    {
        _props.Children.Add(K.H2("Условие"));
        _props.Children.Add(K.Combo(StepCatalog.Conditions.Select(c => (c.Kind, c.Title)), cond.Kind, k =>
        {
            if (k == cond.Kind) return;
            cond.Kind = k;
            cond.Parameters.Clear();
            Dispatcher.BeginInvoke(Changed);
        }));
        _props.Children.Add(K.Check("НЕ (инвертировать условие)", cond.Negate, v => { cond.Negate = v; OnParamChanged(); }));
        foreach (var p in StepCatalog.Get(cond.Kind).Parameters)
            _props.Children.Add(ParamEditor(cond.Parameters, p, OnParamChanged));
    }

    private UIElement ParamEditor(Dictionary<string, string> values, ParamDef p, Action changed)
    {
        values.TryGetValue(p.Key, out var cur);
        void Set(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) values.Remove(p.Key); else values[p.Key] = v;
            changed();
        }
        var label = p.Label + (p.Required ? " *" : "");
        UIElement editor;
        switch (p.Type)
        {
            case ParamType.Bool:
                return K.Check(p.Label, cur is "true" or "True" || (cur is null && p.Default == "true"), v => Set(v ? "true" : "false"), p.Hint);
            case ParamType.Choice when p.Options is { Count: > 0 }:
                editor = K.Combo(p.Options.Select(o => (o.Value, o.Label)), cur ?? p.Default ?? p.Options[0].Value, v => Set(v));
                break;
            case ParamType.App:
                editor = EditableCombo(cur, _c.Catalog.All.Select(a => a.DisplayName).OrderBy(n => n), Set);
                break;
            case ParamType.Template:
                editor = EditableCombo(cur, _c.Templates.All.Where(t => t.Id != _draft?.Id).Select(t => t.Name).OrderBy(n => n), Set);
                break;
            case ParamType.Anchor:
                editor = EditableCombo(cur, _c.Anchors.All.Select(a => a.Label).Where(l => l.Length > 0).OrderBy(n => n), Set);
                break;
            case ParamType.MultilineText:
                editor = BoxLive(cur, Set, multiline: true);
                break;
            case ParamType.KeyChord:
                editor = ChordBox(cur, Set);
                break;
            case ParamType.Path:
                var pathBox = BoxLive(cur, Set);
                editor = K.Stack(pathBox, K.Btn("Обзор…", () =>
                {
                    var dlg = new OpenFileDialog { CheckFileExists = false };
                    if (dlg.ShowDialog() == true) pathBox.Text = dlg.FileName;
                }));
                break;
            default:
                editor = BoxLive(cur, Set, width: p.Type == ParamType.Number ? 140 : double.NaN);
                break;
        }
        return K.Labeled(label, editor, p.Hint);
    }

    private static TextBox ChordBox(string? cur, Action<string?> set)
    {
        var box = BoxLive(cur, set);
        box.ToolTip = "Например: Ctrl+Shift+T. Или щёлкните поле и нажмите сочетание с Ctrl/Alt/Shift/Win.";
        box.PreviewKeyDown += (_, e) =>
        {
            var mods = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (mods == ModifierKeys.None || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                    or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(key.ToString());
            box.Text = string.Join("+", parts);
            e.Handled = true;
        };
        return box;
    }

    private static TextBox BoxLive(string? value, Action<string?> set, bool multiline = false, double width = double.NaN)
    {
        var t = K.Box(value, null, width, multiline);
        t.TextChanged += (_, _) => set(t.Text);
        return t;
    }

    private static ComboBox EditableCombo(string? value, IEnumerable<string> options, Action<string?> set)
    {
        var c = new ComboBox { IsEditable = true, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var o in options.Distinct()) c.Items.Add(o);
        c.Text = value ?? "";
        c.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => set(c.Text)));
        return c;
    }

    // ───────────── шаблон целиком ─────────────

    private void ApplyHeaderFields()
    {
        if (_draft is null) return;
        _draft.Name = _name.Text.Trim();
        _draft.Description = string.IsNullOrWhiteSpace(_desc.Text) ? null : _desc.Text.Trim();
        _draft.VoicePhrases = _phrases.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _draft.Enabled = _enabled.IsChecked == true;
    }

    private bool Validate()
    {
        if (_draft is null) return false;
        ApplyHeaderFields();
        var issues = TemplateValidator.Validate(_draft, _c.Templates);
        _issues.Text = issues.Count == 0 ? "✓ Ошибок не найдено" : string.Join("\n", issues.Select(i => (i.IsError ? "✖ " : "⚠ ") + i.Message));
        _issues.Foreground = issues.Any(i => i.IsError) ? K.B("Danger") : issues.Count > 0 ? K.B("Warn") : K.B("Ok");
        return !issues.Any(i => i.IsError);
    }

    private void Save()
    {
        if (_draft is null) return;
        if (!Validate() && !K.Ask("В шаблоне есть ошибки. Всё равно сохранить?")) return;
        _draft.ModifiedAt = DateTime.Now;
        _c.Templates.Save(_draft);
        _dirty = false;
        _issues.Text = "Сохранено. " + _issues.Text;
    }

    private void TestRun()
    {
        if (_draft is null || !Validate()) { K.Info("Исправьте ошибки перед запуском."); return; }
        var copy = Jarvis.Core.Storage.JsonStore.DeepClone(_draft)!;
        _ = Task.Run(async () =>
        {
            var r = await _c.Executor.RunAsync(copy);
            _c.Reply($"Тест «{copy.Name}»: {r.Message}", r.Status == ExecutionStatus.Completed ? NotifyLevel.Success : NotifyLevel.Warning, speak: false);
            await Dispatcher.InvokeAsync(() => _issues.Text = $"Тестовый запуск: {r.Status} — {r.Message} (шагов: {r.StepsExecuted})");
        });
    }

    private void NewTemplate()
    {
        if (!ConfirmDiscard()) return;
        var t = new Template { Name = _c.Templates.UniqueName("Новый шаблон") };
        _c.Templates.Save(t);
        _dirty = false;
        _draft = null;
        Select(t.Id);
    }

    private void DuplicateTemplate()
    {
        if (_draft is null) return;
        var copy = _c.Templates.Duplicate(_draft.Id);
        _dirty = false;
        Select(copy.Id);
    }

    private void DeleteTemplate()
    {
        if (_draft is null || !K.Ask($"Удалить шаблон «{_draft.Name}»?")) return;
        _c.Templates.Delete(_draft.Id);
        _draft = null;
        _dirty = false;
        RebuildTree();
        ShowProps();
        RefreshList();
    }

    private void Export()
    {
        if (_draft is null) return;
        ApplyHeaderFields();
        var dlg = new SaveFileDialog { Filter = "Шаблон JARVIS (*.json)|*.json", FileName = _draft.Name + ".json" };
        if (dlg.ShowDialog() == true) _c.Templates.Export([_draft], dlg.FileName);
    }

    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "Шаблоны JARVIS (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var list = _c.Templates.Import(dlg.FileName);
            if (list.Count > 0) Select(list[0].Id);
            K.Info($"Импортировано: {list.Count}");
        }
        catch (Exception ex) { K.Info($"Ошибка импорта: {ex.Message}"); }
    }
}