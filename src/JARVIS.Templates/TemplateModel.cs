using Jarvis.Core.Security;

namespace Jarvis.Templates;

public enum StepKind
{
    LaunchApp, SwitchToWindow, CloseWindow, MinimizeWindow, MaximizeWindow,
    WaitForApp, WaitForElement, FindText, ClickElement, Mouse, TypeText, PressKeys,
    OpenUrl, CreateFile, MoveFile, DeleteFile, SetVolume, SystemOperation, Say,
    If, Wait, Repeat, RunTemplate, EndTemplate,
}

public enum ConditionKind
{
    WindowExists, WindowActive, TextOnScreen, ElementExists, FileExists, PreviousStepSucceeded, TimeoutElapsed,
}

public sealed class StepCondition
{
    public ConditionKind Kind { get; set; } = ConditionKind.WindowExists;
    public bool Negate { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
}

/// <summary>
/// Блок шаблона. Это данные, а не код: выполняет их интерпретатор
/// <see cref="TemplateExecutor"/>, произвольный код в шаблонах невозможен.
/// </summary>
public sealed class TemplateStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public StepKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Comment { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
    /// <summary>Условие для «Если» и необязательное условие выхода для «Повтор».</summary>
    public StepCondition? Condition { get; set; }
    public List<TemplateStep> Then { get; set; } = [];
    public List<TemplateStep> Else { get; set; } = [];
    public List<TemplateStep> Body { get; set; } = [];
    /// <summary>Число попыток (по умолчанию из настроек, максимум ограничен настройками).</summary>
    public int? Retries { get; set; }
    public bool ContinueOnError { get; set; }
    /// <summary>Пользователь пометил шаг как опасный (например, «Enter» в мессенджере = отправка).</summary>
    public DangerCategory? MarkedDangerous { get; set; }

    public string? Get(string key) =>
        Parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        Get(key) is { } s ? s is "true" or "True" or "1" or "да" : fallback;

    public TemplateStep Set(string key, string? value)
    {
        if (value is null) Parameters.Remove(key); else Parameters[key] = value;
        return this;
    }

    public TemplateStep DeepCopy(bool newIds = true)
    {
        var c = Jarvis.Core.Storage.JsonStore.DeepClone(this)!;
        if (newIds) Reassign(c);
        return c;
    }

    private static void Reassign(TemplateStep s)
    {
        s.Id = Guid.NewGuid();
        foreach (var x in s.Then.Concat(s.Else).Concat(s.Body)) Reassign(x);
    }
}

public sealed class Template
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Новый шаблон";
    public string? Description { get; set; }
    /// <summary>Голосовые фразы запуска, например «Джарвис, работа».</summary>
    public List<string> VoicePhrases { get; set; } = [];
    public List<TemplateStep> Steps { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    public Template DeepCopy(string? newName = null)
    {
        var c = Jarvis.Core.Storage.JsonStore.DeepClone(this)!;
        c.Id = Guid.NewGuid();
        c.Name = newName ?? Name;
        c.Steps = Steps.Select(s => s.DeepCopy()).ToList();
        c.CreatedAt = c.ModifiedAt = DateTime.Now;
        return c;
    }

    public IEnumerable<TemplateStep> AllSteps() => Flatten(Steps);

    private static IEnumerable<TemplateStep> Flatten(IEnumerable<TemplateStep> steps)
    {
        foreach (var s in steps)
        {
            yield return s;
            foreach (var c in Flatten(s.Then.Concat(s.Else).Concat(s.Body))) yield return c;
        }
    }
}
