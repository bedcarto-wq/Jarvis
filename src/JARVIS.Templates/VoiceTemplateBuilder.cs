using Jarvis.Core.Apps;
using Jarvis.Core.Commands;
using Jarvis.Core.Text;

namespace Jarvis.Templates;

public sealed record StepParseResult(TemplateStep? Step, string? Error, IReadOnlyList<string>? Candidates = null);

/// <summary>Разбор голосовых описаний блоков («запуск chrome», «ожидание 5 секунд»).</summary>
public static class StepPhraseParser
{
    public static StepParseResult Parse(string text, IEnumerable<AppEntry> apps, ITemplateRepository templates)
    {
        var n = TextNormalizer.Normalize(text);
        var appList = apps.ToList();

        if (n is "скриншот" or "снимок экрана" or "скриншота")
            return Ok(StepCatalog.Create(StepKind.SystemOperation).Set(P.Operation, "Screenshot"));
        if (n is "рабочий стол" or "показ рабочего стола")
            return Ok(StepCatalog.Create(StepKind.SystemOperation).Set(P.Operation, "ShowDesktop"));
        if (n is "сворачивание окна" or "сворачивание")
            return Ok(StepCatalog.Create(StepKind.MinimizeWindow));
        if (n is "разворачивание окна" or "разворачивание")
            return Ok(StepCatalog.Create(StepKind.MaximizeWindow));
        if (n is "завершение" or "завершение шаблона")
            return Ok(StepCatalog.Create(StepKind.EndTemplate));

        foreach (var prefix in new[] { "открытие сайта", "открытие сайт", "сайт" })
        {
            if (TextNormalizer.StartsWithWords(n, prefix, out var rest) && rest.Length > 0)
            {
                var url = CommandLexicon.Sites.TryGetValue(rest, out var known) ? known
                    : CommandParser.LooksLikeUrl(rest) ? CommandParser.NormalizeUrl(rest) : null;
                return url is null ? Fail($"Не понимаю адрес «{rest}».") : Ok(StepCatalog.Create(StepKind.OpenUrl).Set(P.Url, url));
            }
        }

        foreach (var prefix in new[] { "ожидание", "паузу", "пауза", "ждать", "подождать" })
        {
            if (!TextNormalizer.StartsWithWords(n, prefix, out var rest) || rest.Length == 0) continue;
            var words = TextNormalizer.Words(rest);
            if (RussianNumbers.TryFind(words, out var num, out var start, out var len))
            {
                var unit = start + len < words.Length ? words[start + len] : "секунд";
                int ms = unit.StartsWith("мин") ? num * 60_000 : unit.StartsWith("милли") ? num : num * 1000;
                return Ok(StepCatalog.Create(StepKind.Wait).Set(P.Ms, ms.ToString()));
            }
            if (rest is "секунду" or "секунда") return Ok(StepCatalog.Create(StepKind.Wait).Set(P.Ms, "1000"));
            var appWait = MatchApp(rest, appList);
            if (appWait.Error is not null) return appWait.Result!;
            return Ok(StepCatalog.Create(StepKind.WaitForApp).Set(P.App, appWait.App!.DisplayName));
        }

        if (TextNormalizer.StartsWithWords(n, "шаблон", out var tplName) && tplName.Length > 0)
        {
            var t = templates.FindByName(tplName);
            return t is null ? Fail($"Шаблон «{tplName}» не найден.") : Ok(StepCatalog.Create(StepKind.RunTemplate).Set(P.Template, t.Name));
        }

        if (TextNormalizer.StartsWithWords(n, "громкость", out var vol) &&
            RussianNumbers.TryFind(TextNormalizer.Words(vol), out var v, out _, out _))
            return Ok(StepCatalog.Create(StepKind.SetVolume).Set(P.Mode, "set").Set(P.Value, Math.Clamp(v, 0, 100).ToString()));

        var appSteps = new (string Prefix, StepKind Kind)[]
        {
            ("запуск программы", StepKind.LaunchApp), ("запуск", StepKind.LaunchApp), ("открытие", StepKind.LaunchApp),
            ("переключение на", StepKind.SwitchToWindow), ("переключение в", StepKind.SwitchToWindow), ("переход в", StepKind.SwitchToWindow),
            ("закрытие", StepKind.CloseWindow), ("сворачивание", StepKind.MinimizeWindow), ("разворачивание", StepKind.MaximizeWindow),
        };
        foreach (var (prefix, kind) in appSteps)
        {
            if (!TextNormalizer.StartsWithWords(n, prefix, out var rest) || rest.Length == 0) continue;
            var m = MatchApp(rest, appList);
            if (m.Error is not null) return m.Result!;
            return Ok(StepCatalog.Create(kind).Set(P.App, m.App!.DisplayName));
        }
        return Fail("Не понял действие. Скажите, например: «добавь запуск Chrome» или «добавь ожидание 5 секунд».");
    }

    private static (AppEntry? App, string? Error, StepParseResult? Result) MatchApp(string rest, List<AppEntry> apps)
    {
        var m = AppMatcher.Match(rest, apps);
        if (m.IsAmbiguous)
        {
            var names = m.Candidates.Select(c => c.App.DisplayName).ToList();
            var err = "Уточните: " + string.Join(" или ", names);
            return (null, err, new StepParseResult(null, err, names));
        }
        if (!m.Found)
        {
            var err = $"Не знаю программу «{rest}».";
            return (null, err, new StepParseResult(null, err));
        }
        return (m.Best!.App, null, null);
    }

    private static StepParseResult Ok(TemplateStep s) => new(s, null);
    private static StepParseResult Fail(string e) => new(null, e);
}

public sealed record BuilderReply(string Speech, bool Finished = false, Template? Saved = null);

/// <summary>
/// Голосовое создание шаблона. Использует ту же модель блоков, что и визуальный редактор.
/// Неоднозначные фразы не добавляются — пользователь получает просьбу уточнить.
/// </summary>
public sealed class VoiceTemplateBuilder
{
    private readonly Func<IEnumerable<AppEntry>> _apps;
    private readonly TemplateStore _store;

    public VoiceTemplateBuilder(Func<IEnumerable<AppEntry>> apps, TemplateStore store)
    {
        _apps = apps;
        _store = store;
    }

    public bool IsActive => Draft is not null;
    public Template? Draft { get; private set; }
    public event Action? Changed;

    public static bool IsBuilderIntent(CommandIntent i) =>
        i is CommandIntent.CreateTemplate or CommandIntent.NameTemplate or CommandIntent.AddStep
            or CommandIntent.SaveTemplate or CommandIntent.CancelTemplate;

    public BuilderReply Handle(ParsedCommand cmd)
    {
        try
        {
            return HandleCore(cmd);
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    private BuilderReply HandleCore(ParsedCommand cmd)
    {
        switch (cmd.Intent)
        {
            case CommandIntent.CreateTemplate:
                if (IsActive) return new BuilderReply("Шаблон уже создаётся. Сохраните или отмените его.");
                Draft = new Template { Name = "" };
                return new BuilderReply("Создаю шаблон. Как его назвать?");
            case CommandIntent.CancelTemplate:
                if (!IsActive) return new BuilderReply("Нет создаваемого шаблона.");
                Draft = null;
                return new BuilderReply("Создание шаблона отменено.", Finished: true);
        }
        if (!IsActive) return new BuilderReply("Сначала скажите «создай шаблон».");
        switch (cmd.Intent)
        {
            case CommandIntent.NameTemplate:
            {
                var name = (cmd.Target ?? "").Trim();
                if (name.Length == 0) return new BuilderReply("Не расслышал название.");
                var existing = _store.FindByName(name);
                if (existing is not null) return new BuilderReply($"Шаблон «{existing.Name}» уже существует. Назовите иначе.");
                Draft!.Name = Capitalize(name);
                Draft.VoicePhrases = [name];
                return new BuilderReply($"Название: {Draft.Name}. Добавляйте действия.");
            }
            case CommandIntent.AddStep:
            {
                var r = StepPhraseParser.Parse(cmd.Target ?? "", _apps(), _store);
                if (r.Step is null) return new BuilderReply(r.Error ?? "Не понял действие.");
                Draft!.Steps.Add(r.Step);
                return new BuilderReply($"Добавлено: {StepCatalog.Describe(r.Step)}.");
            }
            case CommandIntent.SaveTemplate:
            {
                if (string.IsNullOrWhiteSpace(Draft!.Name)) return new BuilderReply("Сначала назовите шаблон: «назови его …».");
                if (Draft.Steps.Count == 0) return new BuilderReply("В шаблоне нет действий. Добавьте хотя бы одно.");
                var t = Draft;
                _store.Save(t);
                Draft = null;
                return new BuilderReply($"Шаблон «{t.Name}» сохранён.", Finished: true, Saved: t);
            }
        }
        return new BuilderReply("Команда не относится к созданию шаблона.");
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}
