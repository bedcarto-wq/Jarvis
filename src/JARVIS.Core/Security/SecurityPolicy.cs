using Jarvis.Core.Settings;

namespace Jarvis.Core.Security;

public sealed record ConfirmationRequest(string Description, DangerCategory Category);

/// <summary>
/// Политика подтверждений. Разрешение на опасное действие может прийти только
/// от пользователя через <see cref="Abstractions.IUserInteraction.ConfirmAsync"/>,
/// текст на экране разрешением не считается.
/// </summary>
public sealed class SecurityPolicy
{
    private readonly Func<JarvisSettings> _settings;

    public SecurityPolicy(Func<JarvisSettings> settings) => _settings = settings;

    public bool RequiresConfirmation(DangerCategory category)
    {
        if (category == DangerCategory.None) return false;
        // Пометка пользователя «опасно» на конкретном шаге всегда требует подтверждения.
        if (category == DangerCategory.Custom) return true;
        var entry = _settings().DangerousOperations.FirstOrDefault(d => d.Category == category);
        return entry?.RequireConfirmation ?? true;
    }

    /// <summary>Категория опасности сочетания клавиш по пользовательскому списку.</summary>
    public DangerCategory ClassifyHotkey(string chord)
    {
        var normalized = KeyChord.TryParse(chord, out var parsed) ? parsed.ToString() : chord;
        foreach (var h in _settings().DangerousHotkeys)
        {
            if (KeyChord.TryParse(h.Chord, out var hp) && hp.ToString() == normalized)
                return h.Category;
        }
        return DangerCategory.None;
    }
}
