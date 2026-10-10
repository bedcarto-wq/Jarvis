using Jarvis.Core.Security;

namespace Jarvis.Core.Settings;

public enum StopReaction
{
    /// <summary>Немедленно прекратить текущий шаблон.</summary>
    StopImmediately,
    /// <summary>Приостановить выполнение (продолжение командой «продолжи»).</summary>
    Pause,
    /// <summary>Отменить только ожидающее подтверждения действие.</summary>
    CancelPendingConfirmation,
    /// <summary>Остановить и выполнить выбранный пользователем шаблон.</summary>
    StopAndRunTemplate,
}

public enum SpeechEngineKind
{
    /// <summary>Vosk (Kaldi) с лёгкой моделью vosk-model-small-ru (~45 МБ), офлайн, ограниченный словарь команд.</summary>
    Vosk,
    /// <summary>CMU PocketSphinx (классическая HMM/GMM-модель), офлайн, русский язык.</summary>
    PocketSphinx,
    /// <summary>Распознаватель Windows SAPI 5 (System.Speech), если установлен для нужного языка.</summary>
    WindowsSapi,
    /// <summary>Голосовой ввод отключён, только текстовые команды.</summary>
    None,
}

public enum HudCorner { TopRight, TopLeft, BottomRight, BottomLeft, Custom }

public sealed class DangerousOperationSetting
{
    public DangerCategory Category { get; set; }
    public bool RequireConfirmation { get; set; } = true;
}

public sealed class DangerousHotkeySetting
{
    public string Chord { get; set; } = "";
    public DangerCategory Category { get; set; } = DangerCategory.Custom;
}

public sealed class JarvisSettings
{
    public const int CurrentSchema = 1;

    // Производительность (см. Jarvis.Core.Performance)
    public string ActiveProfileId { get; set; } = "builtin-medium";
    public Jarvis.Core.Performance.PerformanceSettings Performance { get; set; } = new();
    public bool MonitoringEnabled { get; set; } = true;
    /// <summary>Дополнительное место (например, HDD) для архивов, экспорта и отчётов. Необязательно.</summary>
    public string? ExtraStoragePath { get; set; }

    // Общие
    public bool Autostart { get; set; } = true;
    public bool StartMinimizedToTray { get; set; } = true;
    public bool DebugMode { get; set; }

    // Голос
    public SpeechEngineKind SpeechEngine { get; set; } = SpeechEngineKind.Vosk;
    /// <summary>Версия выбора движка: старые настройки с PocketSphinx по умолчанию один раз переводятся на Vosk.</summary>
    public int? SpeechEngineRevision { get; set; }
    public bool VoiceActivationEnabled { get; set; } = true;
    public bool MicrophoneMuted { get; set; }
    public int MicrophoneDeviceNumber { get; set; } = 0;
    public List<string> WakePhrases { get; set; } = ["джарвис", "окей джарвис", "эй джарвис"];
    public string StopWord { get; set; } = "жёпа";
    public StopReaction StopReaction { get; set; } = StopReaction.StopImmediately;
    public string? StopTemplateName { get; set; }
    /// <summary>Пауза тишины после речи, по которой фраза считается законченной.</summary>
    public int EndOfSpeechTimeoutMs { get; set; } = 800;
    /// <summary>Порог энергии речи (дБFS) для детектора речи.</summary>
    public double VadThresholdDb { get; set; } = -42;
    /// <summary>Сколько секунд после одиночного «Джарвис» ждать команду.</summary>
    public int CommandWindowSeconds { get; set; } = 6;
    /// <summary>Минимальная нормированная оценка гипотезы PocketSphinx (больше — строже).</summary>
    public double RecognitionStrictness { get; set; } = 0.5;
    public string? PocketSphinxModelPath { get; set; }
    public string? VoskModelPath { get; set; }
    public string SapiCulture { get; set; } = "ru-RU";
    public bool BeepOnListen { get; set; } = true;
    public bool BeepOnDone { get; set; } = true;
    public bool VoiceResponses { get; set; } = true;
    public string? TtsVoiceName { get; set; }
    public int TtsRate { get; set; } = 0;
    public int TtsVolume { get; set; } = 90;

    // HUD
    public bool HudEnabled { get; set; } = true;
    public bool HudAnimations { get; set; } = true;
    public HudCorner HudCorner { get; set; } = HudCorner.TopRight;
    public double HudOpacity { get; set; } = 0.92;
    /// <summary>Положение плашки, перетащенной мышью (используется при HudCorner = Custom).</summary>
    public double? HudLeft { get; set; }
    public double? HudTop { get; set; }

    // Выполнение
    public int DefaultRetries { get; set; } = 3;
    public int MaxRetriesCap { get; set; } = 10;
    public int MaxRepeatCount { get; set; } = 100;
    public int MaxTemplateNesting { get; set; } = 8;
    public int LaunchTimeoutMs { get; set; } = 5000;
    public int StepDelayMs { get; set; } = 150;
    public int TypingDelayMs { get; set; } = 5;

    // Зрение
    public bool VisionUseUiAutomation { get; set; } = true;
    public bool VisionUseOcr { get; set; } = true;
    public string OcrLanguages { get; set; } = "rus+eng";
    public bool VisionAllowCoordinateFallback { get; set; } = true;

    // Безопасность
    public List<DangerousOperationSetting> DangerousOperations { get; set; } = DefaultDangerous();
    public List<DangerousHotkeySetting> DangerousHotkeys { get; set; } =
    [
        new() { Chord = "Shift+Delete", Category = DangerCategory.DeleteFiles },
        new() { Chord = "Win+R", Category = DangerCategory.RunScripts },
        new() { Chord = "Win+X", Category = DangerCategory.ChangeSystemSettings },
        new() { Chord = "Ctrl+Shift+Enter", Category = DangerCategory.RunAsAdministrator },
    ];

    public static List<DangerousOperationSetting> DefaultDangerous() =>
        Enum.GetValues<DangerCategory>()
            .Where(c => c != DangerCategory.None)
            .Select(c => new DangerousOperationSetting { Category = c, RequireConfirmation = true })
            .ToList();

    /// <summary>Приводит значения к безопасным границам (после загрузки/редактирования).</summary>
    public JarvisSettings Normalize()
    {
        WakePhrases = (WakePhrases ?? []).Select(p => p.Trim()).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (string.IsNullOrWhiteSpace(StopWord)) StopWord = "жёпа";
        MaxRetriesCap = Math.Clamp(MaxRetriesCap, 1, 10);
        DefaultRetries = Math.Clamp(DefaultRetries, 1, MaxRetriesCap);
        MaxRepeatCount = Math.Clamp(MaxRepeatCount, 1, 1000);
        MaxTemplateNesting = Math.Clamp(MaxTemplateNesting, 1, 16);
        LaunchTimeoutMs = Math.Clamp(LaunchTimeoutMs, 1000, 30000);
        EndOfSpeechTimeoutMs = Math.Clamp(EndOfSpeechTimeoutMs, 300, 3000);
        VadThresholdDb = Math.Clamp(VadThresholdDb, -70, -10);
        CommandWindowSeconds = Math.Clamp(CommandWindowSeconds, 2, 30);
        RecognitionStrictness = Math.Clamp(RecognitionStrictness, 0, 1);
        StepDelayMs = Math.Clamp(StepDelayMs, 0, 5000);
        TypingDelayMs = Math.Clamp(TypingDelayMs, 0, 200);
        TtsRate = Math.Clamp(TtsRate, -10, 10);
        TtsVolume = Math.Clamp(TtsVolume, 0, 100);
        HudOpacity = Math.Clamp(HudOpacity, 0.3, 1.0);
        if (SpeechEngineRevision is null or < 2)
        {
            if (SpeechEngine == SpeechEngineKind.PocketSphinx) SpeechEngine = SpeechEngineKind.Vosk;
            SpeechEngineRevision = 2;
        }
        DangerousOperations ??= DefaultDangerous();
        foreach (var c in Enum.GetValues<DangerCategory>().Where(c => c != DangerCategory.None))
            if (DangerousOperations.All(d => d.Category != c))
                DangerousOperations.Add(new DangerousOperationSetting { Category = c, RequireConfirmation = true });
        DangerousHotkeys ??= [];
        Performance ??= new();
        Performance.Normalize();
        if (string.IsNullOrWhiteSpace(ActiveProfileId)) ActiveProfileId = "builtin-medium";
        return this;
    }
}
