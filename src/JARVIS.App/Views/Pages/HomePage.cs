using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.Core.Logging;
using Jarvis.Core.Settings;
using Jarvis.Voice.Recognition;

namespace Jarvis.App.Views.Pages;

public sealed class HomePage : UserControl, IPage
{
    private readonly JarvisController _c;
    private readonly TextBlock _state = K.Text("", 20);
    private readonly TextBlock _voice = K.Hint("");
    private readonly TextBlock _heard = K.Text("");
    private readonly TextBlock _result = K.Text("");
    private readonly TextBlock _setup = K.Hint("");
    private readonly ObservableCollection<string> _log = new();
    private readonly Button _pause, _disable;

    public HomePage(JarvisController c)
    {
        _c = c;
        var input = K.Box("", width: 520);
        input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Run(input); };
        _pause = K.Btn("Пауза", () => { _c.TogglePause(); Refresh(); }, tip: "Приостановить/продолжить выполнение шаблона");
        _disable = K.Btn("Отключить голос", () => { _c.SetDisabled(!_c.IsDisabled); Refresh(); }, tip: "Аналог «Джарвис, отключись»");
        var logList = new ListBox { ItemsSource = _log, Height = 260, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };

        Content = K.Page(
            K.H1("Главная"),
            K.Card(
                _state, _voice,
                K.Row(new TextBlock { Text = "Последняя команда: ", Foreground = K.B("Muted") }, _heard),
                K.Row(new TextBlock { Text = "Результат: ", Foreground = K.B("Muted") }, _result),
                K.Row(K.Btn("■ Стоп", () => _c.StopEverything("Остановлено"), "DangerButton", "Немедленно остановить всё и отпустить клавиши"),
                      _pause, _disable,
                      K.Btn("Перезапустить голос", () => System.Threading.Tasks.Task.Run(_c.StartVoice)),
                      K.Btn("Скрыть в трей", () => Window.GetWindow(this)?.Hide()))),
            K.Card(_setup),
            K.Card(K.H2("Команда текстом"),
                K.Hint("Та же обработка, что и голосом (фраза активации не нужна). Удобно для проверки без микрофона."),
                K.Row(input, K.Btn("Выполнить", () => Run(input), "GoldButton")),
                K.Hint("Примеры: «открой телеграм», «переключись на хром», «громкость 30», «сверни окно», «запусти шаблон работа», «создай шаблон».")),
            K.Card(K.H2("Журнал (в памяти, последние 500 записей)"), logList));

        foreach (var e in _c.Log.Snapshot().TakeLast(200)) _log.Insert(0, e.ToString());
        _c.Log.EntryAdded += e => Dispatcher.BeginInvoke(() =>
        {
            _log.Insert(0, e.ToString());
            while (_log.Count > 500) _log.RemoveAt(_log.Count - 1);
        });
        _c.StateChanged += _ => Dispatcher.BeginInvoke(Refresh);
        _c.Heard += _ => Dispatcher.BeginInvoke(Refresh);
        _c.Message += (_, _) => Dispatcher.BeginInvoke(Refresh);
    }

    private void Run(TextBox input)
    {
        var text = input.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        input.Clear();
        _ = _c.HandleCommandTextAsync(text, fromVoice: false);
    }

    public void OnShown() => Refresh();

    private void Refresh()
    {
        _state.Text = $"Состояние: {_c.State.ToRussian()}";
        _voice.Text = _c.VoiceStatus;
        _heard.Text = _c.LastHeard ?? "—";
        _result.Text = _c.LastResult ?? "—";
        _pause.Content = _c.Stop.Pause.IsPaused ? "Продолжить" : "Пауза";
        _disable.Content = _c.IsDisabled ? "Включить голос" : "Отключить голос";
        var notes = new List<string>();
        var model = _c.ValidateActiveModel();
        if (model is not null) notes.Add($"• Модель распознавания: {model}. Запустите scripts\\install-models.cmd или см. docs\\VOICE.md.");
        if (_c.Settings.Current.SpeechEngine == SpeechEngineKind.PocketSphinx && !File.Exists(Path.Combine(AppContext.BaseDirectory, "pocketsphinx.dll"))) notes.Add("• Рядом с JARVIS.exe нет pocketsphinx.dll — голосовое распознавание PocketSphinx недоступно (см. docs\\BUILD.md).");
        if (!_c.Tts.HasRussianVoice) notes.Add("• " + _c.Tts.StatusMessage);
        if (!_c.Vision.OcrAvailable) notes.Add($"• OCR: {_c.Vision.OcrStatus}");
        if (!_c.Catalog.IsInitialized) notes.Add("• Идёт первичный поиск программ…");
        _setup.Text = notes.Count == 0 ? "Все компоненты на месте." : "Что нужно настроить:\n" + string.Join("\n", notes);
    }
}
