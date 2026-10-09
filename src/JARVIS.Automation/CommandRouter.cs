using Jarvis.Core.Commands;
using Jarvis.Core.Text;
using Jarvis.Templates;

namespace Jarvis.Automation;

public enum ControlAction { Stop, Pause, Resume, Disable, Enable, Confirm, Deny, Undo, Remember, PointHere, Help, Benchmark }

public abstract record RouteResult;
public sealed record RunStepsRoute(string Name, IReadOnlyList<TemplateStep> Steps) : RouteResult;
public sealed record RunTemplateRoute(Template Template) : RouteResult;
public sealed record SpeakRoute(string Text, bool IsError = false) : RouteResult;
public sealed record ControlRoute(ControlAction Action, string? Argument = null) : RouteResult;
public sealed record BuilderRoute(BuilderReply Reply) : RouteResult;
public sealed record ClarifyRoute(string Question, ParsedCommand Command, IReadOnlyList<string> Options) : RouteResult;
public sealed record NothingRoute : RouteResult;

/// <summary>
/// Превращает распознанную команду в действие. Прямые команды становятся
/// одношаговыми шаблонами и проходят ту же проверку безопасности и стоп.
/// </summary>
public sealed class CommandRouter
{
    private readonly ITemplateRepository _templates;
    private readonly VoiceTemplateBuilder _builder;

    public CommandRouter(ITemplateRepository templates, VoiceTemplateBuilder builder)
    {
        _templates = templates;
        _builder = builder;
    }

    public RouteResult Route(ParsedCommand cmd, string? rawCommandText = null)
    {
        static TemplateStep Step(StepKind k, params (string Key, string Value)[] p)
        {
            var s = StepCatalog.Create(k);
            foreach (var (key, value) in p) s.Parameters[key] = value;
            return s;
        }
        RouteResult One(string name, TemplateStep s) => new RunStepsRoute(name, [s]);
        string App() => cmd.App!.Best!.App.DisplayName;

        if (VoiceTemplateBuilder.IsBuilderIntent(cmd.Intent))
            return new BuilderRoute(_builder.Handle(cmd));

        switch (cmd.Intent)
        {
            case CommandIntent.Empty: return new NothingRoute();
            case CommandIntent.Unknown: return new SpeakRoute(cmd.Message ?? "Не понял команду.", true);
            case CommandIntent.Ambiguous:
                return new ClarifyRoute(cmd.Message ?? "Уточните команду.", cmd,
                    cmd.App?.Candidates.Select(c => c.App.DisplayName).ToList() ?? []);
            case CommandIntent.OpenApp: return One($"Открыть {App()}", Step(StepKind.LaunchApp, (P.App, App())));
            case CommandIntent.SwitchTo: return One($"Переключиться на {App()}", Step(StepKind.SwitchToWindow, (P.App, App()), (P.LaunchIfMissing, "false")));
            case CommandIntent.CloseApp: return One($"Закрыть {App()}", Step(StepKind.CloseWindow, (P.App, App())));
            case CommandIntent.CloseActive: return One("Закрыть окно", Step(StepKind.CloseWindow));
            case CommandIntent.MinimizeActive: return One("Свернуть окно", Step(StepKind.MinimizeWindow));
            case CommandIntent.MinimizeApp: return One($"Свернуть {App()}", Step(StepKind.MinimizeWindow, (P.App, App())));
            case CommandIntent.MaximizeActive: return One("Развернуть окно", Step(StepKind.MaximizeWindow));
            case CommandIntent.MaximizeApp: return One($"Развернуть {App()}", Step(StepKind.MaximizeWindow, (P.App, App())));
            case CommandIntent.ShowDesktop: return One("Рабочий стол", Step(StepKind.SystemOperation, (P.Operation, "ShowDesktop")));
            case CommandIntent.OpenSite: return One($"Открыть {cmd.Url}", Step(StepKind.OpenUrl, (P.Url, cmd.Url!)));
            case CommandIntent.NewTab: return One("Новая вкладка", Step(StepKind.PressKeys, (P.Keys, "Ctrl+T")));
            case CommandIntent.NextTab: return One("Следующая вкладка", Step(StepKind.PressKeys, (P.Keys, "Ctrl+Tab")));
            case CommandIntent.PrevTab: return One("Предыдущая вкладка", Step(StepKind.PressKeys, (P.Keys, "Ctrl+Shift+Tab")));
            case CommandIntent.CloseTab: return One("Закрыть вкладку", Step(StepKind.PressKeys, (P.Keys, "Ctrl+W")));
            case CommandIntent.Copy: return One("Копировать", Step(StepKind.PressKeys, (P.Keys, "Ctrl+C")));
            case CommandIntent.Paste: return One("Вставить", Step(StepKind.PressKeys, (P.Keys, "Ctrl+V")));
            case CommandIntent.SelectAll: return One("Выделить всё", Step(StepKind.PressKeys, (P.Keys, "Ctrl+A")));
            case CommandIntent.VolumeUp: return One("Громче", Step(StepKind.SetVolume, (P.Mode, "up"), (P.Value, "10")));
            case CommandIntent.VolumeDown: return One("Тише", Step(StepKind.SetVolume, (P.Mode, "down"), (P.Value, "10")));
            case CommandIntent.Mute: return One("Без звука", Step(StepKind.SetVolume, (P.Mode, "mute")));
            case CommandIntent.Unmute: return One("Включить звук", Step(StepKind.SetVolume, (P.Mode, "unmute")));
            case CommandIntent.SetVolume: return One($"Громкость {cmd.Number}", Step(StepKind.SetVolume, (P.Mode, "set"), (P.Value, cmd.Number!.Value.ToString())));
            case CommandIntent.Screenshot: return One("Скриншот", Step(StepKind.SystemOperation, (P.Operation, "Screenshot")));
            case CommandIntent.TypeText:
            {
                var text = ExtractRawTail(rawCommandText, cmd.Normalized, cmd.Target!) ?? cmd.Target!;
                return One("Ввод текста", Step(StepKind.TypeText, (P.Text, text)));
            }
            case CommandIntent.RunTemplate:
            {
                var t = cmd.TemplateId is not null && Guid.TryParse(cmd.TemplateId, out var id) ? _templates.Get(id) : _templates.FindByName(cmd.Target ?? "");
                return t is null ? new SpeakRoute("Шаблон не найден.", true) : new RunTemplateRoute(t);
            }
            case CommandIntent.Stop: return new ControlRoute(ControlAction.Stop);
            case CommandIntent.Pause: return new ControlRoute(ControlAction.Pause);
            case CommandIntent.Resume: return new ControlRoute(ControlAction.Resume);
            case CommandIntent.Disable: return new ControlRoute(ControlAction.Disable);
            case CommandIntent.Enable: return new ControlRoute(ControlAction.Enable);
            case CommandIntent.Confirm: return new ControlRoute(ControlAction.Confirm);
            case CommandIntent.Deny: return new ControlRoute(ControlAction.Deny);
            case CommandIntent.Undo: return new ControlRoute(ControlAction.Undo);
            case CommandIntent.Remember: return new ControlRoute(ControlAction.Remember, cmd.Target);
            case CommandIntent.PointHere: return new ControlRoute(ControlAction.PointHere);
            case CommandIntent.Help: return new ControlRoute(ControlAction.Help);
            case CommandIntent.Benchmark: return new ControlRoute(ControlAction.Benchmark);
            default: return new SpeakRoute("Не понял команду.", true);
        }
    }

    /// <summary>Для ввода текста берём исходный регистр и пунктуацию из напечатанной команды.</summary>
    internal static string? ExtractRawTail(string? raw, string normalized, string normalizedTail)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int prefixWords = TextNormalizer.Words(normalized).Length - TextNormalizer.Words(normalizedTail).Length;
        var trimmed = raw.Trim();
        int idx = 0, words = 0;
        // Пропускаем слова префикса (и фразу активации, если она в начале исходного текста).
        int rawWordCount = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int normWordCount = TextNormalizer.Words(TextNormalizer.Normalize(trimmed)).Length;
        int skip = prefixWords + Math.Max(0, normWordCount - TextNormalizer.Words(normalized).Length);
        if (rawWordCount != normWordCount) return null; // пунктуация-слова: не рискуем
        while (idx < trimmed.Length && words < skip)
        {
            while (idx < trimmed.Length && char.IsWhiteSpace(trimmed[idx])) idx++;
            while (idx < trimmed.Length && !char.IsWhiteSpace(trimmed[idx])) idx++;
            words++;
        }
        var tail = trimmed[idx..].Trim();
        return tail.Length == 0 ? null : tail;
    }
}
