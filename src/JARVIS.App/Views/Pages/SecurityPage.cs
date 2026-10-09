using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;

namespace Jarvis.App.Views.Pages;

public sealed class SecurityPage : UserControl, IPage
{
    private readonly JarvisController _c;
    private readonly ListBox _undo = new() { Height = 120 };

    public SecurityPage(JarvisController c)
    {
        _c = c;
        var s = _c.Settings.Current;
        var ops = new StackPanel();
        foreach (var op in s.DangerousOperations)
        {
            var cat = op.Category;
            ops.Children.Add(K.Check($"Спрашивать подтверждение: {cat.ToRussian()}", op.RequireConfirmation,
                v => _c.Settings.Update(x => x.DangerousOperations.First(o => o.Category == cat).RequireConfirmation = v)));
        }
        var hotkeys = K.Box(string.Join(Environment.NewLine, s.DangerousHotkeys.Select(h => h.Chord)), v => _c.Settings.Update(x =>
            x.DangerousHotkeys = v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ch => new DangerousHotkeySetting { Chord = ch, Category = DangerCategory.Custom }).ToList()), multiline: true);

        Content = K.Page(
            K.H1("Безопасность"),
            K.Card(K.H2("Опасные действия"),
                K.Hint("Перед такими действиями JARVIS спросит подтверждение (голосом «да/нет» или кнопкой). " +
                       "Действия, отмеченные в шаблоне как опасные вручную, подтверждаются всегда."),
                ops),
            K.Card(K.H2("Опасные сочетания клавиш"),
                K.Hint("По одному в строке, например: Shift+Delete, Win+R, Alt+F4. Нажатие таких сочетаний из шаблона требует подтверждения."),
                hotkeys),
            K.Card(K.H2("Журнал отмены"),
                K.Hint("Обратимые действия (перемещение и создание файлов) можно отменить командой «Джарвис, отмени» или кнопкой ниже. " +
                       "Удаление выполняется только в корзину."),
                _undo,
                K.Row(K.Btn("Отменить последнее", UndoLast))),
            K.Card(K.H2("Конфиденциальность"),
                K.Text("• Звук обрабатывается в памяти и нигде не сохраняется.\n• Нет телеметрии, облака и сетевых запросов, кроме тех, что вы сами задали (открыть сайт).\n" +
                       "• Журнал хранится только в памяти; на диск — лишь в режиме отладки (папка logs).\n• Пароли не запоминаются и не вводятся автоматически.\n" +
                       "• JARVIS не обходит UAC и не запрашивает права администратора.")));
    }

    public void OnShown()
    {
        _undo.Items.Clear();
        if (_c.Undo.PeekDescription is { } d) _undo.Items.Add($"Последнее: {d}");
        else _undo.Items.Add("Нет действий для отмены");
    }

    private void UndoLast()
    {
        var u = _c.Undo.UndoLast();
        if (u is null) { K.Info("Нечего отменять"); return; }
        try { u.Undo(); K.Info($"Отменено: {u.Description}"); }
        catch (Exception ex) { K.Info($"Не удалось: {ex.Message}"); }
        OnShown();
    }
}
