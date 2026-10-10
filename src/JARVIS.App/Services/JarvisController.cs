using System.Diagnostics;
using System.IO;
using Jarvis.Automation;
using Jarvis.Core;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Apps;
using Jarvis.Core.Commands;
using Jarvis.Core.Execution;
using Jarvis.Core.Logging;
using Jarvis.Core.Performance;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Jarvis.Core.Text;
using Jarvis.Core.Vision;
using Jarvis.Platform;
using Jarvis.Templates;
using Jarvis.Vision;
using Jarvis.Voice.Recognition;
using Jarvis.Voice.Session;

namespace Jarvis.App.Services;

/// <summary>
/// Корень композиции и «мозг» JARVIS: голос → разбор → маршрут → исполнение,
/// реакции на слово-стоп, отключение/включение, «Запомни», состояние HUD.
/// </summary>
public sealed partial class JarvisController : IDisposable
{
    public AppPaths Paths { get; }
    public JarvisLog Log { get; } = new();
    public SettingsService Settings { get; }
    public AppCatalog Catalog { get; }
    public AnchorStore Anchors { get; }
    public TemplateStore Templates { get; }
    public WindowService Windows { get; } = new();
    public InputService Input { get; } = new();
    public AppDiscovery Discovery { get; }
    public VisionService Vision { get; }
    public WpfUserInteraction Ui { get; } = new();
    public SapiTts Tts { get; }
    public Earcons Earcons { get; } = new();
    public UndoJournal Undo { get; } = new();
    public StopController Stop { get; }
    public SecurityPolicy Security { get; }
    public AutomationServices Services { get; }
    public ActionRunner Runner { get; }
    public TemplateExecutor Executor { get; }
    public VoiceTemplateBuilder Builder { get; }
    public CommandParser Parser { get; }
    public CommandRouter Router { get; }
    public AudioCapture Audio { get; } = new();
    public VoiceSession? Voice { get; private set; }
    public ISpeechEngine? Engine { get; private set; }

    private TesseractOcr? _ocr;
    private readonly System.Threading.Timer _grammarTimer;
    private bool _error;
    private string _voiceStatus = "Голос не запущен";

    public HudState State { get; private set; } = HudState.Idle;
    public string VoiceStatus => _voiceStatus;
    public string? LastHeard { get; private set; }
    public string? LastResult { get; private set; }
    public double LastRecognitionMs { get; private set; }
    public double LastCommandMs { get; private set; }
    public double MicLevelDb { get; private set; } = -100;

    public event Action<HudState>? StateChanged;
    public event Action<string, NotifyLevel>? Message;
    public event Action<string>? Heard;
    public event Action<double>? MicLevel;
    public event Action<string>? StepText;

    public JarvisController(AppPaths? paths = null)
    {
        Paths = paths ?? new AppPaths();
        Paths.EnsureCreated();
        Settings = new SettingsService(Paths.SettingsFile, Paths.CorruptDir);
        Settings.Load();
        Log.DebugEnabled = Settings.Current.DebugMode;
        Log.DebugFileDirectory = Paths.LogsDir;
        if (Settings.LastLoadMessage is { } sm) Log.Warn(sm);

        Catalog = new AppCatalog(Paths.AppsFile, Paths.CorruptDir);
        if (Catalog.Load() is { } cm) Log.Warn(cm);
        Anchors = new AnchorStore(Paths.AnchorsFile, Paths.CorruptDir);
        Anchors.Load();
        if (Anchors.LastLoadMessage is { } am) Log.Warn(am);
        Templates = new TemplateStore(Paths.TemplatesDir, Paths.CorruptDir);
        foreach (var m in Templates.Load()) Log.Warn(m);

        Profiles = new PerformanceProfileService(Paths.ProfilesDir, Paths.CorruptDir);
        foreach (var m in Profiles.Load()) Log.Warn(m);
        Gate = new BackgroundGate(() => Settings.Current.Performance.MaxBackgroundTasks);
        Windows.CacheMs = () => Settings.Current.Performance.WindowListCacheMs;
        Discovery = new AppDiscovery(Log, () => Settings.Current.Performance.MaxBackgroundTasks);
        Vision = new VisionService(Windows, Input, Anchors, GetOcr, Log, () => Settings.Current.Performance);
        var s = Settings.Current;
        Tts = new SapiTts(s.TtsVoiceName, s.TtsRate, s.TtsVolume, Log);
        Earcons.Enabled = s.BeepOnListen || s.BeepOnDone;
        Ui.Input = () => Input;
        Ui.Notified += (msg, lvl) => Message?.Invoke(msg, lvl);
        Ui.StateChanged += RecomputeState;

        Stop = new StopController(Input);
        Security = new SecurityPolicy(() => Settings.Current);
        Services = new AutomationServices
        {
            Windows = Windows, Launcher = new AppLauncher(), Discovery = Discovery, Catalog = Catalog, Input = Input,
            Vision = Vision, Files = new FileOperations(), Volume = new VolumeService(), System = new SystemOperations(),
            Speech = Tts, Ui = Ui, Anchors = Anchors, Undo = Undo, Settings = () => Settings.Current, Paths = Paths, Log = Log,
        };
        Runner = new ActionRunner(Services);
        Executor = new TemplateExecutor(Templates, Runner, Security, Ui, Stop, () => Settings.Current, null, Log);
        Executor.Event += OnExecutionEvent;
        Builder = new VoiceTemplateBuilder(() => Catalog.All, Templates);
        Parser = new CommandParser(() => Catalog.All, Templates.Phrases, () => Settings.Current.WakePhrases);
        Router = new CommandRouter(Templates, Builder);
        Stop.Pause.Changed += _ => RecomputeState();

        _grammarTimer = new System.Threading.Timer(_ => RefreshGrammar(), null, Timeout.Infinite, Timeout.Infinite);
        Catalog.Changed += ScheduleGrammarRefresh;
        Templates.Changed += ScheduleGrammarRefresh;
        Settings.Changed += OnSettingsChanged;
        Ui.StateChanged += OnUiPromptChanged;
        InitPerformance();
    }

    private TesseractOcr? GetOcr()
    {
        var s = Settings.Current;
        if (!s.VisionUseOcr) return null;
        return _ocr ??= new TesseractOcr(Paths.TessdataDir, s.OcrLanguages, Log, () => Settings.Current.Performance.OcrScale);
    }

    // ───────────── запуск ─────────────

    public async Task StartAsync()
    {
        Log.Info($"JARVIS {typeof(JarvisController).Assembly.GetName().Version} запущен. Данные: {Paths.Root}");
        if (!Catalog.IsInitialized) _ = Gate.RunAsync(() => Task.Run(FirstRunScanAsync));
        if (Settings.Current.MonitoringEnabled) Monitoring.Start();
        await Task.Run(StartVoice);
        RecomputeState();
    }

    private async Task FirstRunScanAsync()
    {
        try
        {
            Notify("Первый запуск: ищу установленные программы…");
            var found = await Discovery.DiscoverAsync(CancellationToken.None);
            var added = Catalog.Merge(found);
            Notify($"Готово: найдено программ — {found.Count}, добавлено новых — {added}.", NotifyLevel.Success);
        }
        catch (Exception ex)
        {
            Log.Error($"Поиск программ: {ex.Message}");
        }
    }

    public async Task<int> RescanAppsAsync()
    {
        var found = await Gate.RunAsync(() => Discovery.DiscoverAsync(CancellationToken.None));
        return Catalog.Merge(found);
    }

    public string PocketSphinxModelDir =>
        string.IsNullOrWhiteSpace(Settings.Current.PocketSphinxModelPath)
            ? Path.Combine(Paths.ModelsDir, "cmusphinx-ru-5.2")
            : Settings.Current.PocketSphinxModelPath!;

    public string VoskModelDir =>
        string.IsNullOrWhiteSpace(Settings.Current.VoskModelPath)
            ? Path.Combine(Paths.ModelsDir, VoskEngine.DefaultModelName)
            : Settings.Current.VoskModelPath!;

    /// <summary>Папка модели выбранного движка (для подсказок в интерфейсе).</summary>
    public string? ValidateActiveModel() => Settings.Current.SpeechEngine switch
    {
        SpeechEngineKind.Vosk => VoskEngine.ValidateModel(VoskModelDir),
        SpeechEngineKind.PocketSphinx => PocketSphinxEngine.ValidateModel(PocketSphinxModelDir) is { } p && !p.Contains("ru.dic") ? p : null,
        _ => null,
    };

    public void StartVoice()
    {
        StopVoice();
        var s = Settings.Current;
        if (!s.VoiceActivationEnabled || s.SpeechEngine == SpeechEngineKind.None)
        {
            _voiceStatus = "Голосовое управление выключено в настройках";
            RecomputeState();
            return;
        }
        ISpeechEngine engine = s.SpeechEngine switch
        {
            SpeechEngineKind.WindowsSapi => new SapiRecognizer(s.SapiCulture, Log) { MinConfidence = 0.3 + 0.5 * s.RecognitionStrictness },
            SpeechEngineKind.PocketSphinx => new PocketSphinxEngine(PocketSphinxModelDir, Paths.ModelsDir, Log)
            {
                Strictness = s.RecognitionStrictness,
                MaxHmmPf = s.Performance.RecognizerMaxHmmPf,
                BeamExp = s.Performance.RecognizerBeamExp,
            },
            _ => new VoskEngine(VoskModelDir, Log) { Strictness = s.RecognitionStrictness },
        };
        _voiceTuning = (s.Performance.RecognizerMaxHmmPf, s.Performance.RecognizerBeamExp);
        try
        {
            var sw = Stopwatch.StartNew();
            engine.Initialize(BuildGrammar());
            Log.Info($"Распознаватель «{engine.Name}» готов за {sw.ElapsedMilliseconds} мс");
        }
        catch (Exception ex)
        {
            _voiceStatus = $"Голос недоступен: {ex.Message}";
            Log.Error(_voiceStatus);
            engine.Dispose();
            _error = true;
            RecomputeState();
            Notify(_voiceStatus + " Текстовые команды и шаблоны работают.", NotifyLevel.Error);
            return;
        }
        Engine = engine;
        var session = new VoiceSession(Audio, engine, OptionsFrom(s), Log)
        {
            IsSpeaking = () => Tts.IsSpeaking,
            Muted = s.MicrophoneMuted,
        };
        session.StateChanged += _ => RecomputeState();
        session.LevelChanged += db => { MicLevelDb = db; MicLevel?.Invoke(db); };
        session.WakeHeard += () =>
        {
            if (Settings.Current.BeepOnListen) Earcons.ListenStart();
            Message?.Invoke("Слушаю…", NotifyLevel.Info);
        };
        session.CommandHeard += text => _ = HandleCommandTextAsync(text, fromVoice: true);
        session.StopWordHeard += _ => OnStopWord();
        session.EnableRequested += () => SetDisabled(false);
        session.ErrorOccurred += msg => { _error = true; _voiceStatus = msg; RecomputeState(); Notify(msg, NotifyLevel.Error); };
        session.Ignored += t => Log.Debug($"Проигнорировано (нет фразы активации): «{t}»");
        Voice = session;
        _error = false;
        session.Start(s.MicrophoneDeviceNumber);
        if (_error) return;
        _voiceStatus = $"{engine.Name}: {engine.Status}";
        RecomputeState();
    }

    public void StopVoice()
    {
        var v = Voice;
        Voice = null;
        Engine = null;
        if (v is not null)
        {
            v.Stop();
            // VoiceSession.Dispose освобождает и микрофон, и движок; микрофон используем повторно.
            try { v.Dispose(); } catch { }
        }
    }

    private static VoiceSessionOptions OptionsFrom(JarvisSettings s) => new()
    {
        WakePhrases = s.WakePhrases,
        StopWord = s.StopWord,
        CommandWindowSeconds = s.CommandWindowSeconds,
        VadThresholdDb = s.VadThresholdDb,
        EndSilenceMs = s.EndOfSpeechTimeoutMs,
    };

    public GrammarSpec BuildGrammar() =>
        VocabularyBuilder.Build(Settings.Current, Catalog.All, Templates.Phrases().Select(p => p.Phrase),
            Anchors.All.Select(a => a.Label).Concat(Templates.All.Select(t => t.Name)));

    private void ScheduleGrammarRefresh() => _grammarTimer.Change(1500, Timeout.Infinite);

    private void RefreshGrammar()
    {
        try { Engine?.UpdateGrammar(BuildGrammar()); Log.Debug("Словарь распознавания обновлён"); }
        catch (Exception ex) { Log.Warn($"Обновление словаря: {ex.Message}"); }
    }

    private JarvisSettings _lastVoiceSettings = new();

    private void OnSettingsChanged(JarvisSettings s)
    {
        Log.DebugEnabled = s.DebugMode;
        Tts.Configure(s.TtsVoiceName, s.TtsRate, s.TtsVolume);
        Earcons.Enabled = s.BeepOnListen || s.BeepOnDone;
        _ocr?.Dispose();
        _ocr = null;
        if (Voice is not null)
        {
            Voice.UpdateOptions(OptionsFrom(s));
            Voice.Muted = s.MicrophoneMuted;
        }
        if (Engine is PocketSphinxEngine ps) ps.Strictness = s.RecognitionStrictness;
        if (Engine is VoskEngine vk) vk.Strictness = s.RecognitionStrictness;
        OnPerformanceSettingsChanged(s);
        ScheduleGrammarRefresh();
        RecomputeState();
    }

    // ───────────── команды ─────────────

    /// <summary>Единая точка входа для голосовых и текстовых команд (фраза активации уже снята).</summary>
    public async Task HandleCommandTextAsync(string text, bool fromVoice)
    {
        var raw = text.Trim();
        if (!fromVoice && WakePhraseDetector.TryStrip(raw, Settings.Current.WakePhrases, out var rest)) raw = rest;
        if (!fromVoice && WakePhraseDetector.ContainsStopWord(raw, Settings.Current.StopWord)) { OnStopWord(); return; }
        LastHeard = raw;
        Heard?.Invoke(raw);
        Log.Info($"Команда: «{raw}»");
        var sw = Stopwatch.StartNew();
        try
        {
            var cmd = Parser.Parse(raw);
            if (TryAnswerBenchmarkPrompt(cmd)) return;
            // Ответ на ожидающий вопрос имеет приоритет.
            if (Ui.AwaitingConfirmation && cmd.Intent is CommandIntent.Confirm or CommandIntent.Deny)
            {
                Ui.AnswerConfirmation(cmd.Intent == CommandIntent.Confirm);
                return;
            }
            if (Ui.AwaitingPoint && cmd.Intent == CommandIntent.PointHere) { Ui.AnswerPointWithCursor(); return; }
            if (Ui.AwaitingChoice && RussianNumbers.TryParse(raw, out var n) && n >= 1 && n <= Ui.CurrentOptions.Count)
            {
                Ui.AnswerChoice(n - 1);
                return;
            }
            if (Ui.AwaitingChoice && cmd.Intent is CommandIntent.Deny or CommandIntent.Stop) { Ui.AnswerChoice(null); return; }

            await ExecuteRouteAsync(Router.Route(cmd, raw), cmd, raw);
        }
        catch (Exception ex)
        {
            Log.Error($"Ошибка выполнения: {ex}");
            Reply($"Ошибка: {ex.Message}", NotifyLevel.Error, speech: VoiceReplies.Cannot);
        }
        finally
        {
            LastCommandMs = sw.Elapsed.TotalMilliseconds;
            if (Voice is not null) Voice.ExpectingReply = false;
            RecomputeState();
        }
    }

    private async Task ExecuteRouteAsync(RouteResult route, ParsedCommand cmd, string raw)
    {
        switch (route)
        {
            case NothingRoute:
                return;
            case SpeakRoute sr:
                Reply(sr.Text, sr.IsError ? NotifyLevel.Warning : NotifyLevel.Info,
                    speech: sr.IsError ? (cmd.Intent is CommandIntent.Unknown or CommandIntent.Empty ? VoiceReplies.Repeat : VoiceReplies.Cannot) : null);
                return;
            case RunStepsRoute rs:
                await RunAsync(() => Executor.RunStepsAsync(rs.Name, rs.Steps));
                return;
            case RunTemplateRoute rt:
                Reply($"Запускаю «{rt.Template.Name}»", NotifyLevel.Info, speech: VoiceReplies.Working);
                await RunAsync(() => Executor.RunAsync(rt.Template));
                return;
            case BuilderRoute br:
                Reply(br.Reply.Speech, NotifyLevel.Info);
                if (Voice is not null) Voice.ExpectingReply = Builder.IsActive;
                return;
            case ClarifyRoute cr:
                Reply(cr.Question, NotifyLevel.Warning);
                if (cr.Options.Count == 0) return;
                if (Voice is not null) Voice.ExpectingReply = true;
                var idx = await Ui.ChooseAsync(cr.Question, cr.Options, Stop.RunToken);
                if (idx is null) { Reply("Отменено", NotifyLevel.Info); return; }
                var chosen = cr.Command.App?.Candidates.ElementAtOrDefault(idx.Value);
                if (chosen is null) return;
                var resolved = cr.Command with { App = new AppMatchResult(chosen, [chosen], false) };
                await ExecuteRouteAsync(Router.Route(resolved, raw), resolved, raw);
                return;
            case ControlRoute ctl:
                await HandleControlAsync(ctl);
                return;
        }
    }

    private async Task RunAsync(Func<Task<ExecutionResult>> run)
    {
        RecomputeState();
        var r = await run();
        switch (r.Status)
        {
            case ExecutionStatus.Completed:
                LastResult = r.Message;
                if (Settings.Current.BeepOnDone) Earcons.CommandDone();
                Reply(r.Message, NotifyLevel.Success, speech: VoiceReplies.Done);
                break;
            case ExecutionStatus.Busy:
                Reply(r.Message + " Скажите «стоп», чтобы прервать.", NotifyLevel.Warning, speech: VoiceReplies.Cannot);
                break;
            case ExecutionStatus.Stopped:
            case ExecutionStatus.Cancelled:
                LastResult = r.Message;
                Message?.Invoke(r.Message, NotifyLevel.Warning);
                break;
            default:
                LastResult = r.Message;
                Earcons.Error();
                Reply(r.Message, NotifyLevel.Error, speech: VoiceReplies.Cannot);
                break;
        }
    }

    private async Task HandleControlAsync(ControlRoute ctl)
    {
        switch (ctl.Action)
        {
            case ControlAction.Stop:
                StopEverything(VoiceReplies.Stopped);
                break;
            case ControlAction.Pause:
                Stop.Pause.Pause();
                Input.ReleaseAll();
                Reply("Пауза", NotifyLevel.Info);
                break;
            case ControlAction.Resume:
                Stop.Pause.Resume();
                Reply("Продолжаю", NotifyLevel.Info);
                break;
            case ControlAction.Disable:
                SetDisabled(true);
                break;
            case ControlAction.Enable:
                SetDisabled(false);
                break;
            case ControlAction.Confirm:
            case ControlAction.Deny:
                if (!Ui.AnswerConfirmation(ctl.Action == ControlAction.Confirm)) Reply("Нечего подтверждать", NotifyLevel.Info);
                break;
            case ControlAction.Undo:
                var u = Undo.UndoLast();
                if (u is null) Reply("Нечего отменять", NotifyLevel.Info);
                else
                {
                    try { u.Undo(); Reply($"Отменено: {u.Description}", NotifyLevel.Success); }
                    catch (Exception ex) { Reply($"Не удалось отменить: {ex.Message}", NotifyLevel.Error); }
                }
                break;
            case ControlAction.Remember:
                await RememberUnderCursorAsync(ctl.Argument);
                break;
            case ControlAction.PointHere:
                if (!Ui.AnswerPointWithCursor()) Reply("Я сейчас ничего не ищу", NotifyLevel.Info);
                break;
            case ControlAction.Benchmark:
                RequestBenchmarkFromVoice();
                break;
            case ControlAction.Help:
                Reply("Скажите: «Джарвис, открой Телеграм», «переключись на Хром», «громкость 30», «запусти шаблон …», " +
                      "«создай шаблон», «запомни это как …», «отключись». Слово-стоп: «" + Settings.Current.StopWord + "».", NotifyLevel.Info);
                break;
        }
    }

    /// <summary>«Джарвис, запомни это как …»: элемент под курсором сохраняется как привязка.</summary>
    public async Task<ElementAnchor?> RememberUnderCursorAsync(string? label)
    {
        await Task.Delay(100);
        var pos = Input.GetCursorPosition();
        var el = Vision.GetElementAt(pos);
        if (el is null)
        {
            Reply("Не вижу элемента под курсором. Сохраню только координаты.", NotifyLevel.Warning);
            el = new UiElementInfo(null, null, null, null, new ScreenRect(pos.X - 10, pos.Y - 10, 20, 20), null, null, null, false);
        }
        var name = string.IsNullOrWhiteSpace(label) ? el.Name ?? $"Элемент {Anchors.All.Count + 1}" : label.Trim();
        var appId = el.ProcessName is null ? null : Catalog.All.FirstOrDefault(a =>
            a.ProcessNames.Contains(el.ProcessName, StringComparer.OrdinalIgnoreCase))?.Id;
        var anchor = AnchorStore.FromElement(el, pos, name, appId);
        Anchors.Upsert(anchor);
        Reply($"Запомнил «{name}»", NotifyLevel.Success);
        return anchor;
    }

    // ───────────── стоп, пауза, отключение ─────────────

    private void OnStopWord()
    {
        var s = Settings.Current;
        Log.Info($"Слово-стоп, реакция: {s.StopReaction}");
        switch (s.StopReaction)
        {
            case StopReaction.Pause:
                Stop.Pause.Pause();
                Input.ReleaseAll();
                Tts.Cancel();
                Message?.Invoke("Пауза (слово-стоп). Скажите «Джарвис, продолжи».", NotifyLevel.Warning);
                break;
            case StopReaction.CancelPendingConfirmation:
                Stop.CancelPendingConfirmation();
                Ui.AnswerConfirmation(false);
                Input.ReleaseAll();
                Message?.Invoke("Ожидающее действие отменено", NotifyLevel.Warning);
                break;
            case StopReaction.StopAndRunTemplate:
                StopEverything(VoiceReplies.Stopped);
                var t = s.StopTemplateName is null ? null : Templates.FindByName(s.StopTemplateName);
                if (t is not null)
                    _ = Task.Run(async () => { await Task.Delay(300); await RunAsync(() => Executor.RunAsync(t)); });
                break;
            default:
                StopEverything(VoiceReplies.Stopped);
                break;
        }
        RecomputeState();
    }

    public void StopEverything(string message)
    {
        Stop.StopAll();
        Ui.CancelAll();
        Tts.Cancel();
        Input.ReleaseAll();
        CancelBenchmark();
        if (Builder.IsActive) { /* черновик голосового шаблона сохраняется до «отмена» */ }
        Message?.Invoke(message, NotifyLevel.Warning);
        if (message == VoiceReplies.Stopped && Settings.Current.VoiceResponses && Tts.IsAvailable) Tts.Say(VoiceReplies.Stopped);
        RecomputeState();
    }

    public bool IsDisabled => Voice?.SoftDisabled == true || _softDisabled;
    private bool _softDisabled;

    public void SetDisabled(bool disabled)
    {
        _softDisabled = disabled;
        Voice?.SetSoftDisabled(disabled);
        if (disabled)
        {
            Stop.StopAll();
            Reply("Отключаюсь. Скажите «Джарвис, включись».", NotifyLevel.Info);
        }
        else Reply("Снова на связи", NotifyLevel.Success);
        RecomputeState();
    }

    public void TogglePause()
    {
        if (Stop.Pause.IsPaused) Stop.Pause.Resume();
        else { Stop.Pause.Pause(); Input.ReleaseAll(); }
        RecomputeState();
    }

    // ───────────── состояние и ответы ─────────────

    private void OnExecutionEvent(ExecutionEvent e)
    {
        switch (e.Kind)
        {
            case ExecutionEventKind.StepStarted:
                StepText?.Invoke(e.Step ?? "");
                break;
            case ExecutionEventKind.StepRetry:
            case ExecutionEventKind.StepFailed:
                Log.Warn($"{e.TemplateName}: {e.Step} — {e.Message}");
                break;
            case ExecutionEventKind.Started:
            case ExecutionEventKind.Completed:
            case ExecutionEventKind.Failed:
            case ExecutionEventKind.Stopped:
            case ExecutionEventKind.Cancelled:
                Log.Info($"{e.TemplateName}: {e.Kind} {e.Message}");
                break;
        }
        RecomputeState();
    }

    /// <param name="speech">Короткая голосовая фраза (см. <see cref="VoiceReplies"/>); подробности — только на экране.</param>
    public void Reply(string text, NotifyLevel level, bool speak = true, string? speech = null)
    {
        Message?.Invoke(text, level);
        if (speak && Settings.Current.VoiceResponses && Tts.IsAvailable) Tts.Say(speech ?? Shorten(text));
    }

    private bool _wasConfirm, _wasPoint;

    private void OnUiPromptChanged()
    {
        bool c = Ui.AwaitingConfirmation, p = Ui.AwaitingPoint;
        if (Settings.Current.VoiceResponses && Tts.IsAvailable)
        {
            if (c && !_wasConfirm) Tts.Say(VoiceReplies.Confirm);
            else if (p && !_wasPoint) Tts.Say(VoiceReplies.PointCursor);
        }
        _wasConfirm = c;
        _wasPoint = p;
    }

    private static string Shorten(string text)
    {
        var dot = text.IndexOfAny(['.', '!', '?']);
        return dot > 0 && dot < 120 ? text[..(dot + 1)] : text.Length > 140 ? text[..140] : text;
    }

    private void Notify(string text, NotifyLevel level = NotifyLevel.Info) => Message?.Invoke(text, level);

    public void RecomputeState()
    {
        var v = Voice;
        HudState s;
        if (Ui.AwaitingConfirmation) s = HudState.AwaitingConfirmation;
        else if (Ui.AwaitingPoint || Ui.AwaitingChoice) s = HudState.NeedsHelp;
        else if (Stop.Pause.IsPaused) s = HudState.Paused;
        else if (Executor.IsRunning) s = HudState.Executing;
        else if (IsDisabled) s = HudState.Disabled;
        else if (_error) s = HudState.Error;
        else s = v?.State switch
        {
            VoiceState.Listening => HudState.Listening,
            VoiceState.Recognizing => HudState.Recognizing,
            VoiceState.Error => HudState.Error,
            _ => HudState.Idle,
        };
        if (s == State) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    public void ClearError() { _error = false; RecomputeState(); }

    public void Dispose()
    {
        _grammarTimer.Dispose();
        Monitoring.Dispose();
        StopEverything("Выход");
        StopVoice();
        Audio.Dispose();
        Tts.Dispose();
        Vision.Dispose();
    }
}
