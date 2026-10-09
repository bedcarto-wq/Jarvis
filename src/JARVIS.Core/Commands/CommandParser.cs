using Jarvis.Core.Apps;
using Jarvis.Core.Text;

namespace Jarvis.Core.Commands;

public sealed record TemplatePhrase(string TemplateId, string TemplateName, string Phrase);

/// <summary>
/// Детерминированный разбор команд по словарю. Никаких языковых моделей:
/// либо команда однозначно распознана, либо возвращается Unknown/Ambiguous.
/// </summary>
public sealed class CommandParser
{
    private readonly Func<IEnumerable<AppEntry>> _apps;
    private readonly Func<IEnumerable<TemplatePhrase>> _templates;
    private readonly Func<IEnumerable<string>> _wakePhrases;
    private readonly List<(string Phrase, CommandIntent Intent)> _prefixes;

    public CommandParser(Func<IEnumerable<AppEntry>> apps, Func<IEnumerable<TemplatePhrase>> templates,
        Func<IEnumerable<string>>? wakePhrases = null)
    {
        _apps = apps;
        _templates = templates;
        _wakePhrases = wakePhrases ?? (() => []);
        _prefixes = CommandLexicon.Prefixes.OrderByDescending(p => p.Phrase.Length).ToList();
    }

    /// <summary>Разбирает команду (фраза активации уже удалена).</summary>
    public ParsedCommand Parse(string text)
    {
        var n = TextNormalizer.Normalize(text);
        if (n.Length == 0) return new ParsedCommand(CommandIntent.Empty, n);

        // 1. Голосовая фраза шаблона («работа»).
        var tpl = MatchTemplatePhrase(n);
        if (tpl is not null) return new ParsedCommand(CommandIntent.RunTemplate, n, tpl.TemplateName, TemplateId: tpl.TemplateId);

        // 2. Фиксированные фразы.
        if (CommandLexicon.Fixed.TryGetValue(n, out var fixedIntent)) return new ParsedCommand(fixedIntent, n);

        // 3. Команды с параметром.
        foreach (var (phrase, intent) in _prefixes)
        {
            if (!TextNormalizer.StartsWithWords(n, phrase, out var rest) || rest.Length == 0) continue;
            return ParseWithArgument(intent, n, rest);
        }
        return new ParsedCommand(CommandIntent.Unknown, n,
            Message: "Такой команды нет. Создайте шаблон для этой задачи.");
    }

    private ParsedCommand ParseWithArgument(CommandIntent intent, string n, string rest)
    {
        switch (intent)
        {
            case CommandIntent.OpenApp:
            {
                if (LooksLikeUrl(rest)) return new ParsedCommand(CommandIntent.OpenSite, n, rest, Url: NormalizeUrl(rest));
                var m = AppMatcher.Match(rest, _apps());
                if (m.IsAmbiguous) return Ambiguous(n, rest, m);
                if (m.Found) return new ParsedCommand(CommandIntent.OpenApp, n, rest, App: m);
                if (CommandLexicon.Sites.TryGetValue(rest, out var site))
                    return new ParsedCommand(CommandIntent.OpenSite, n, rest, Url: site);
                var t = MatchTemplateName(rest);
                if (t is not null) return new ParsedCommand(CommandIntent.RunTemplate, n, t.TemplateName, TemplateId: t.TemplateId);
                return new ParsedCommand(CommandIntent.Unknown, n, rest,
                    Message: $"Не знаю программу «{rest}». Добавьте её в разделе «Приложения».");
            }
            case CommandIntent.SwitchTo:
            case CommandIntent.CloseApp:
            case CommandIntent.MinimizeApp:
            case CommandIntent.MaximizeApp:
            {
                var m = AppMatcher.Match(rest, _apps());
                if (m.IsAmbiguous) return Ambiguous(n, rest, m);
                if (m.Found) return new ParsedCommand(intent, n, rest, App: m);
                return new ParsedCommand(CommandIntent.Unknown, n, rest, Message: $"Не знаю программу «{rest}».");
            }
            case CommandIntent.OpenSite:
            {
                if (CommandLexicon.Sites.TryGetValue(rest, out var site))
                    return new ParsedCommand(CommandIntent.OpenSite, n, rest, Url: site);
                if (LooksLikeUrl(rest)) return new ParsedCommand(CommandIntent.OpenSite, n, rest, Url: NormalizeUrl(rest));
                return new ParsedCommand(CommandIntent.Unknown, n, rest, Message: $"Не понимаю адрес «{rest}».");
            }
            case CommandIntent.RunTemplate:
            {
                var t = MatchTemplateName(rest);
                return t is null
                    ? new ParsedCommand(CommandIntent.Unknown, n, rest, Message: $"Шаблон «{rest}» не найден.")
                    : new ParsedCommand(CommandIntent.RunTemplate, n, t.TemplateName, TemplateId: t.TemplateId);
            }
            case CommandIntent.SetVolume:
            {
                if (RussianNumbers.TryFind(TextNormalizer.Words(rest), out var v, out _, out _))
                    return new ParsedCommand(CommandIntent.SetVolume, n, rest, Number: Math.Clamp(v, 0, 100));
                return new ParsedCommand(CommandIntent.Unknown, n, rest, Message: "Назовите уровень громкости от 0 до 100.");
            }
            default:
                return new ParsedCommand(intent, n, rest);
        }
    }

    private static ParsedCommand Ambiguous(string n, string rest, AppMatchResult m) =>
        new(CommandIntent.Ambiguous, n, rest, App: m,
            Message: "Уточните: " + string.Join(" или ", m.Candidates.Select(c => c.App.DisplayName)));

    private TemplatePhrase? MatchTemplatePhrase(string n)
    {
        foreach (var t in _templates())
        {
            var p = TextNormalizer.Normalize(t.Phrase);
            if (WakePhraseDetector.TryStrip(p, _wakePhrases(), out var stripped)) p = stripped;
            if (p.Length > 0 && p == n) return t;
        }
        return null;
    }

    private TemplatePhrase? MatchTemplateName(string rest)
    {
        var list = _templates().ToList();
        return list.FirstOrDefault(t => TextNormalizer.Normalize(t.TemplateName) == rest)
               ?? list.FirstOrDefault(t =>
               {
                   var p = TextNormalizer.Normalize(t.Phrase);
                   if (WakePhraseDetector.TryStrip(p, _wakePhrases(), out var s)) p = s;
                   return p == rest;
               });
    }

    public static bool LooksLikeUrl(string s)
    {
        s = SpokenToUrl(s);
        if (s.Contains(' ')) return false;
        if (s.StartsWith("http://") || s.StartsWith("https://")) return true;
        var dot = s.IndexOf('.');
        return dot > 0 && dot < s.Length - 2;
    }

    public static string SpokenToUrl(string s) =>
        TextNormalizer.Normalize(s).Replace(" точка ", ".").Replace(" dot ", ".").Replace("точка ", ".").Trim();

    public static string NormalizeUrl(string s)
    {
        s = SpokenToUrl(s);
        if (s.StartsWith("http://") || s.StartsWith("https://")) return s;
        return "https://" + s;
    }
}
