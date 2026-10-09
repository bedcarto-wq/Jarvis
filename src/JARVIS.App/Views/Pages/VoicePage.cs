using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.Core.Settings;
using Jarvis.Platform;
using Jarvis.Voice.Recognition;

namespace Jarvis.App.Views.Pages;

public sealed class VoicePage : UserControl, IPage
{
    private readonly JarvisController _c;
    private readonly TextBlock _modelStatus = K.Hint("");
    private readonly ProgressBar _level = new() { Height = 10, Minimum = -70, Maximum = -10, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _levelText = K.Hint("");

    public VoicePage(JarvisController c)
    {
        _c = c;
        var s = _c.Settings.Current;
        void U(Action<JarvisSettings> a) => _c.Settings.Update(a);

        var devices = _c.Audio.GetDevices();
        var mic = K.Combo(devices.Count == 0 ? [(0, "Микрофон не найден")] : devices.Select(d => (d.Number, d.Name)), s.MicrophoneDeviceNumber,
            v => { U(x => x.MicrophoneDeviceNumber = v); });
        var engine = K.Combo(new (SpeechEngineKind, string)[]
        {
            (SpeechEngineKind.PocketSphinx, "CMU PocketSphinx (рекомендуется, офлайн, без нейросетей)"),
            (SpeechEngineKind.WindowsSapi, "Распознаватель Windows SAPI (нужен русский распознаватель Windows)"),
            (SpeechEngineKind.None, "Выключено (только текстовые команды)"),
        }, s.SpeechEngine, v => U(x => x.SpeechEngine = v), 460);
        var model = K.Box(s.PocketSphinxModelPath ?? "", v => { U(x => x.PocketSphinxModelPath = string.IsNullOrWhiteSpace(v) ? null : v.Trim()); UpdateModelStatus(); });
        var wake = K.Box(string.Join(Environment.NewLine, s.WakePhrases), v => U(x => x.WakePhrases =
            v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()), multiline: true);
        var stopWord = K.Box(s.StopWord, v => U(x => x.StopWord = v.Trim()), 240);
        var reaction = K.Combo(new (StopReaction, string)[]
        {
            (StopReaction.StopImmediately, "Немедленно остановить текущий шаблон"),
            (StopReaction.Pause, "Поставить выполнение на паузу"),
            (StopReaction.CancelPendingConfirmation, "Отменить только ожидающее подтверждения действие"),
            (StopReaction.StopAndRunTemplate, "Остановить и запустить выбранный шаблон"),
        }, s.StopReaction, v => U(x => x.StopReaction = v), 420);
        var stopTpl = K.Combo(new[] { ("", "(не выбран)") }.Concat(_c.Templates.All.Select(t => (t.Name, t.Name))), s.StopTemplateName ?? "",
            v => U(x => x.StopTemplateName = v.Length == 0 ? null : v));
        var sapiCultures = SapiRecognizer.InstalledCultures();
        var voices = _c.Tts.GetVoices();

        Content = K.Page(
            K.H1("Голос"),
            K.Card(K.H2("Распознавание речи"),
                K.Labeled("Движок", engine),
                K.Labeled("Папка модели PocketSphinx (пусто = %LOCALAPPDATA%\\JARVIS\\models\\cmusphinx-ru-5.2)", model), _modelStatus,
                K.Hint($"Установленные распознаватели Windows SAPI: {(sapiCultures.Count == 0 ? "нет" : string.Join(", ", sapiCultures))}"),
                K.Labeled("Микрофон", mic),
                K.Row(_level), _levelText,
                K.Check("Микрофон выключен (JARVIS не слушает)", s.MicrophoneMuted, v => U(x => x.MicrophoneMuted = v)),
                K.Check("Голосовая активация включена", s.VoiceActivationEnabled, v => U(x => x.VoiceActivationEnabled = v)),
                K.Labeled("Порог громкости речи, дБ (−60 чувствительнее … −25 грубее)", K.Number(s.VadThresholdDb, v => U(x => x.VadThresholdDb = v), -70, -15)),
                K.Labeled("Пауза окончания фразы, мс", K.Number(s.EndOfSpeechTimeoutMs, v => U(x => x.EndOfSpeechTimeoutMs = (int)v), 300, 3000)),
                K.Labeled("Окно команды после «Джарвис», с", K.Number(s.CommandWindowSeconds, v => U(x => x.CommandWindowSeconds = (int)v), 2, 30)),
                K.Labeled("Строгость распознавания (0 — принимать всё, 1 — только уверенное)", K.Number(s.RecognitionStrictness, v => U(x => x.RecognitionStrictness = v), 0, 1)),
                K.Row(K.Btn("Применить и перезапустить распознавание", () => Task.Run(_c.StartVoice), "GoldButton"))),
            K.Card(K.H2("Фразы активации и слово-стоп"),
                K.Labeled("Фразы активации (по одной в строке)", wake, "По умолчанию: «Джарвис», «Окей Джарвис», «Эй Джарвис»."),
                K.Labeled("Слово-стоп (работает всегда, даже без «Джарвис»)", stopWord),
                K.Labeled("Реакция на слово-стоп", reaction),
                K.Labeled("Шаблон для реакции «остановить и запустить»", stopTpl),
                K.Hint("При любой реакции JARVIS отпускает все удерживаемые клавиши и кнопки мыши.")),
            K.Card(K.H2("Ответы"),
                K.Check("Говорить короткие ответы голосом", s.VoiceResponses, v => U(x => x.VoiceResponses = v)),
                K.Check("Звук начала прослушивания", s.BeepOnListen, v => U(x => x.BeepOnListen = v)),
                K.Check("Звук завершения команды", s.BeepOnDone, v => U(x => x.BeepOnDone = v)),
                K.Labeled("Голос синтеза", K.Combo(new[] { ("", "(автоматически: русский, если есть)") }.Concat(voices.Select(v => (v, v))), s.TtsVoiceName ?? "",
                    v => U(x => x.TtsVoiceName = v.Length == 0 ? null : v), 420)),
                K.Hint(_c.Tts.StatusMessage),
                K.Labeled("Скорость (−10…10)", K.Number(s.TtsRate, v => U(x => x.TtsRate = (int)v), -10, 10)),
                K.Labeled("Громкость (0…100)", K.Number(s.TtsVolume, v => U(x => x.TtsVolume = (int)v), 0, 100)),
                K.Row(K.Btn("Проверить голос", () => _c.Tts.Say("Слушаю, сэр.")))));

        _c.MicLevel += db => Dispatcher.BeginInvoke(() =>
        {
            _level.Value = Math.Clamp(db, -70, -10);
            _levelText.Text = $"Уровень: {db:F0} дБ (порог {_c.Settings.Current.VadThresholdDb:F0} дБ)";
        });
        UpdateModelStatus();
    }

    public void OnShown() => UpdateModelStatus();

    private void UpdateModelStatus()
    {
        var dir = _c.PocketSphinxModelDir;
        var problem = PocketSphinxEngine.ValidateModel(dir);
        var dll = File.Exists(Path.Combine(AppContext.BaseDirectory, "pocketsphinx.dll"));
        _modelStatus.Text = (problem is null ? $"Модель найдена: {dir}" : $"⚠ {problem}") +
                            (dll ? "" : "\n⚠ Не найден pocketsphinx.dll рядом с программой") +
                            $"\nСостояние: {_c.VoiceStatus}";
    }
}
