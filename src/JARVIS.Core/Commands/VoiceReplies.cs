namespace Jarvis.Core.Commands;

/// <summary>Короткие голосовые ответы. Длинные пояснения показываются только на экране.</summary>
public static class VoiceReplies
{
    public const string Done = "Готово";
    public const string Working = "Выполняю";
    public const string Cannot = "Не могу";
    public const string Repeat = "Повторите команду";
    public const string PointCursor = "Наведите курсор";
    public const string Confirm = "Подтвердите действие";
    public const string Stopped = "Остановлено";

    public static readonly IReadOnlyList<string> All = [Done, Working, Cannot, Repeat, PointCursor, Confirm, Stopped];
}
