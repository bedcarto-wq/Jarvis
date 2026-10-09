namespace Jarvis.Templates;

public sealed record ValidationIssue(Guid? StepId, string Message, bool IsError);

public static class TemplateValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(Template t, ITemplateRepository repo)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(t.Name)) issues.Add(new(null, "Не задано название.", true));
        if (t.Steps.Count == 0) issues.Add(new(null, "Шаблон не содержит действий.", false));
        foreach (var s in t.AllSteps())
        {
            var d = StepCatalog.Get(s.Kind);
            foreach (var p in d.Parameters.Where(p => p.Required))
                if (s.Get(p.Key) is null)
                    issues.Add(new(s.Id, $"«{d.Title}»: не заполнено поле «{p.Label}».", true));
            if (s.Kind is StepKind.SwitchToWindow && s.Get(P.App) is null && s.Get(P.Title) is null)
                issues.Add(new(s.Id, "«Переключение на окно»: укажите программу или заголовок.", true));
            if (s.Kind is StepKind.WaitForElement or StepKind.ClickElement
                && s.Get(P.Element) is null && s.Get(P.AutomationId) is null && s.Get(P.Anchor) is null)
                issues.Add(new(s.Id, $"«{d.Title}»: укажите элемент, AutomationId или привязку.", true));
            if (s.Kind == StepKind.If && s.Condition is null)
                issues.Add(new(s.Id, "Условие не задано.", true));
            if (s.Kind == StepKind.PressKeys && s.Get(P.Keys) is { } k && !Jarvis.Core.KeyChord.TryParse(k, out _))
                issues.Add(new(s.Id, $"Не удалось разобрать сочетание «{k}».", true));
            if (s.Kind == StepKind.RunTemplate && s.Get(P.Template) is { } name)
            {
                var target = repo.FindByName(name);
                if (target is null) issues.Add(new(s.Id, $"Шаблон «{name}» не найден.", true));
                else if (target.Id == t.Id) issues.Add(new(s.Id, "Шаблон вызывает сам себя.", true));
                else if (Reaches(target, t.Id, repo, [])) issues.Add(new(s.Id, $"Циклический вызов через «{target.Name}».", true));
            }
        }
        return issues;
    }

    private static bool Reaches(Template from, Guid targetId, ITemplateRepository repo, HashSet<Guid> visited)
    {
        if (!visited.Add(from.Id)) return false;
        foreach (var s in from.AllSteps().Where(s => s.Kind == StepKind.RunTemplate))
        {
            var t = s.Get(P.Template) is { } n ? repo.FindByName(n) : null;
            if (t is null) continue;
            if (t.Id == targetId || Reaches(t, targetId, repo, visited)) return true;
        }
        return false;
    }
}
