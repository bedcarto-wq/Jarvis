using System.Windows;
using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.Core.Vision;

namespace Jarvis.App.Views.Pages;

/// <summary>«Запомненные элементы» — привязки, созданные командой «Запомни».</summary>
public sealed class AnchorsPage : UserControl, IPage
{
    private readonly JarvisController _c;
    private readonly ListBox _list = new() { Height = 320 };
    private readonly TextBox _label = K.Box(""), _text = K.Box("");
    private readonly TextBlock _details = K.Hint("");
    private ElementAnchor? _current;

    public AnchorsPage(JarvisController c)
    {
        _c = c;
        _list.SelectionChanged += (_, _) => Load((_list.SelectedItem as ListBoxItem)?.Tag as ElementAnchor);
        Content = K.Page(
            K.H1("Запомненные элементы"),
            K.Hint("Наведите курсор на кнопку или поле и скажите «Джарвис, запомни это как отправить». " +
                   "Сохраняются имя элемента UI Automation, AutomationId, видимый текст и относительные координаты."),
            _list,
            K.Row(K.Btn("Запомнить элемент под курсором (через 3 с)", RememberDelayed, "GoldButton"),
                  K.Btn("Проверить поиск", Test),
                  K.Btn("Удалить", Remove, "DangerButton"),
                  K.Btn("Очистить всё", Clear, "DangerButton")),
            K.Card(K.H2("Свойства"), K.Labeled("Название (по нему обращаются голосом и в шаблонах)", _label),
                K.Labeled("Видимый текст (для OCR)", _text), _details, K.Row(K.Btn("Сохранить", Save))));
        _c.Anchors.Changed += () => Dispatcher.BeginInvoke(Refresh);
    }

    public void OnShown() => Refresh();

    private void Refresh()
    {
        _list.Items.Clear();
        foreach (var a in _c.Anchors.All.OrderBy(a => a.Label))
            _list.Items.Add(new ListBoxItem
            {
                Content = $"{a}   —   {a.ProcessName ?? "?"} · {a.ControlType ?? "элемент"} · успехов {a.SuccessCount}, неудач {a.FailureCount}",
                Tag = a,
            });
    }

    private void Load(ElementAnchor? a)
    {
        _current = a;
        if (a is null) return;
        _label.Text = a.Label;
        _text.Text = a.VisibleText ?? "";
        _details.Text = $"Окно: {a.WindowTitle}\nИмя UIA: {a.ElementName}\nAutomationId: {a.AutomationId}\nКласс: {a.ClassName}\n" +
                        $"Относительно окна: {a.RelativeX:P1} × {a.RelativeY:P1}\nЭкран: {a.ScreenX}, {a.ScreenY}\nСоздан: {a.CreatedAt:g}, использован: {a.LastUsedAt:g}";
    }

    private void Save()
    {
        if (_current is null) return;
        _current.Label = _label.Text.Trim();
        _current.VisibleText = string.IsNullOrWhiteSpace(_text.Text) ? null : _text.Text.Trim();
        _c.Anchors.Upsert(_current);
    }

    private async void RememberDelayed()
    {
        var label = string.IsNullOrWhiteSpace(_label.Text) || _current is not null ? null : _label.Text;
        _c.Reply("Наведите курсор на элемент, запомню через 3 секунды", Core.Abstractions.NotifyLevel.Info, speak: false);
        await Task.Delay(3000);
        await _c.RememberUnderCursorAsync(label);
    }

    private async void Test()
    {
        if (_current is null) return;
        var r = await _c.Vision.LocateAsync(new ElementQuery { AnchorId = _current.Id }, CancellationToken.None);
        K.Info(r.Found ? $"Найден: {r.Detail}\nПрямоугольник: {r.Bounds}" : $"Не найден: {r.Detail}");
        if (r.Found) _c.Input.MoveMouse(r.Bounds.Center);
    }

    private void Remove()
    {
        if (_current is not null && K.Ask($"Удалить «{_current}»?")) _c.Anchors.Remove(_current.Id);
    }

    private void Clear()
    {
        if (K.Ask("Удалить все запомненные элементы?")) _c.Anchors.Clear();
    }
}
