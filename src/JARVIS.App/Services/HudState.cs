namespace Jarvis.App.Services;

/// <summary>Состояния JARVIS, отображаемые в HUD и трее.</summary>
public enum HudState
{
    Idle, Listening, Recognizing, Executing, AwaitingConfirmation, NeedsHelp, Paused, Disabled, Error,
}

public static class HudStateText
{
    public static string ToRussian(this HudState s) => s switch
    {
        HudState.Idle => "Ожидание",
        HudState.Listening => "Слушаю",
        HudState.Recognizing => "Распознаю",
        HudState.Executing => "Выполняю",
        HudState.AwaitingConfirmation => "Жду подтверждения",
        HudState.NeedsHelp => "Нужна помощь",
        HudState.Paused => "Пауза",
        HudState.Disabled => "Отключён",
        HudState.Error => "Ошибка",
        _ => s.ToString(),
    };
}
