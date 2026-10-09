using Jarvis.Core.Abstractions;
using Jarvis.Core.Security;

namespace Jarvis.Templates;

public enum ParamType { Text, MultilineText, App, Number, Choice, KeyChord, Path, Bool, Template, Anchor, Url }

public sealed record ParamDef(string Key, string Label, ParamType Type, bool Required = false, string? Default = null,
    IReadOnlyList<(string Value, string Label)>? Options = null, string? Hint = null);

public sealed record StepDescriptor(StepKind Kind, string Title, string Group, string Description,
    IReadOnlyList<ParamDef> Parameters, bool IsContainer = false, bool HasCondition = false);

public sealed record ConditionDescriptor(ConditionKind Kind, string Title, IReadOnlyList<ParamDef> Parameters);

/// <summary>Описание всех типов блоков: подписи, параметры, группы, опасность.</summary>
public static class StepCatalog
{
    private static readonly (string, string)[] ClickOptions = [("single", "Одинарный"), ("double", "Двойной"), ("right", "Правой кнопкой")];

    public static readonly IReadOnlyList<StepDescriptor> All =
    [
        new(StepKind.LaunchApp, "Запуск приложения", "Окна и программы", "Запускает программу или переключается на её окно, проверяет запуск.",
            [new(P.App, "Программа", ParamType.App, true), new(P.NewInstance, "Всегда новый экземпляр", ParamType.Bool, Default: "false")]),
        new(StepKind.SwitchToWindow, "Переключение на окно", "Окна и программы", "Активирует окно программы или окно с указанным заголовком.",
            [new(P.App, "Программа", ParamType.App), new(P.Title, "Часть заголовка окна", ParamType.Text),
             new(P.LaunchIfMissing, "Запустить, если не запущена", ParamType.Bool, Default: "false")]),
        new(StepKind.CloseWindow, "Закрытие окна", "Окна и программы", "Закрывает окна программы (или активное окно).",
            [new(P.App, "Программа (пусто — активное окно)", ParamType.App)]),
        new(StepKind.MinimizeWindow, "Свернуть окно", "Окна и программы", "Сворачивает окно программы или активное окно.",
            [new(P.App, "Программа (пусто — активное окно)", ParamType.App)]),
        new(StepKind.MaximizeWindow, "Развернуть окно", "Окна и программы", "Разворачивает окно программы или активное окно.",
            [new(P.App, "Программа (пусто — активное окно)", ParamType.App)]),
        new(StepKind.WaitForApp, "Ожидание программы", "Ожидание", "Ждёт появления окна программы.",
            [new(P.App, "Программа", ParamType.App, true), new(P.TimeoutMs, "Тайм-аут, мс", ParamType.Number, Default: "10000")]),
        new(StepKind.WaitForElement, "Ожидание элемента", "Ожидание", "Ждёт появления элемента интерфейса.",
            [new(P.App, "Программа", ParamType.App), new(P.Element, "Название элемента / текст", ParamType.Text),
             new(P.AutomationId, "AutomationId", ParamType.Text), new(P.Anchor, "Запомненный элемент", ParamType.Anchor),
             new(P.TimeoutMs, "Тайм-аут, мс", ParamType.Number, Default: "10000")]),
        new(StepKind.FindText, "Поиск текста на экране", "Зрение", "Ищет текст через UI Automation, затем OCR. Результат доступен условию «Предыдущий шаг успешен».",
            [new(P.Text, "Текст", ParamType.Text, true), new(P.App, "В окне программы (пусто — активное окно)", ParamType.App)]),
        new(StepKind.ClickElement, "Нажатие элемента", "Зрение", "Находит элемент (UI Automation → привязка → OCR → координаты) и нажимает его.",
            [new(P.App, "Программа", ParamType.App), new(P.Element, "Название элемента / текст", ParamType.Text),
             new(P.AutomationId, "AutomationId", ParamType.Text), new(P.Anchor, "Запомненный элемент", ParamType.Anchor),
             new(P.Click, "Нажатие", ParamType.Choice, Default: "single", Options: ClickOptions)]),
        new(StepKind.Mouse, "Действие мышью", "Мышь и клавиатура", "Перемещение, клик, двойной клик, правая кнопка, перетаскивание.",
            [new(P.Action, "Действие", ParamType.Choice, true, "click",
                [("move", "Переместить"), ("click", "Клик"), ("double", "Двойной клик"), ("right", "Правый клик"), ("drag", "Перетащить")]),
             new(P.X, "X", ParamType.Number, true), new(P.Y, "Y", ParamType.Number, true),
             new(P.X2, "X2 (для перетаскивания)", ParamType.Number), new(P.Y2, "Y2 (для перетаскивания)", ParamType.Number),
             new(P.Relative, "Координаты относительно", ParamType.Choice, false, "window",
                [("window", "Активного окна"), ("screen", "Экрана")])]),
        new(StepKind.TypeText, "Ввод текста", "Мышь и клавиатура", "Печатает текст в активное окно.",
            [new(P.Text, "Текст", ParamType.MultilineText, true)]),
        new(StepKind.PressKeys, "Нажатие клавиш", "Мышь и клавиатура", "Нажимает клавишу или сочетание, например Ctrl+T.",
            [new(P.Keys, "Сочетание", ParamType.KeyChord, true, Hint: "Ctrl+Shift+T, Alt+Tab, Enter, F5"),
             new(P.HoldMs, "Удерживать, мс", ParamType.Number, Default: "0")]),
        new(StepKind.OpenUrl, "Открытие сайта", "Интернет", "Открывает адрес в браузере по умолчанию.",
            [new(P.Url, "Адрес", ParamType.Url, true)]),
        new(StepKind.CreateFile, "Создание файла", "Файлы", "Создаёт текстовый файл.",
            [new(P.Path, "Путь", ParamType.Path, true), new(P.Content, "Содержимое", ParamType.MultilineText),
             new(P.Overwrite, "Перезаписать существующий", ParamType.Bool, Default: "false")]),
        new(StepKind.MoveFile, "Перемещение / переименование файла", "Файлы", "Перемещает или переименовывает файл/папку (отменяемо).",
            [new(P.Source, "Откуда", ParamType.Path, true), new(P.Destination, "Куда", ParamType.Path, true)]),
        new(StepKind.DeleteFile, "Удаление файла (в корзину)", "Файлы", "Удаляет в корзину. Требует подтверждения, если удаление в списке опасных.",
            [new(P.Path, "Путь", ParamType.Path, true)]),
        new(StepKind.SetVolume, "Изменение громкости", "Система", "Громкость системы.",
            [new(P.Mode, "Режим", ParamType.Choice, true, "set",
                [("set", "Установить"), ("up", "Громче"), ("down", "Тише"), ("mute", "Выключить звук"), ("unmute", "Включить звук")]),
             new(P.Value, "Значение / шаг, %", ParamType.Number, Default: "10")]),
        new(StepKind.SystemOperation, "Системная операция", "Система", "Безопасные системные действия; выключение и перезагрузка требуют подтверждения.",
            [new(P.Operation, "Операция", ParamType.Choice, true, "Screenshot",
                [("Screenshot", "Снимок экрана"), ("ShowDesktop", "Показать рабочий стол"), ("LockScreen", "Заблокировать экран"),
                 ("OpenSettings", "Открыть параметры Windows"), ("OpenTaskManager", "Диспетчер задач"), ("Sleep", "Спящий режим"),
                 ("Restart", "Перезагрузка"), ("Shutdown", "Выключение"), ("EmptyRecycleBin", "Очистить корзину")]),
             new(P.Argument, "Параметр (раздел параметров, напр. sound)", ParamType.Text)]),
        new(StepKind.Say, "Сказать фразу", "Система", "Голосовой ответ JARVIS.",
            [new(P.Text, "Фраза", ParamType.Text, true)]),
        new(StepKind.If, "Условие (если / иначе)", "Логика", "ЕСЛИ условие выполнено — ветка «То», ИНАЧЕ — ветка «Иначе».",
            [], IsContainer: true, HasCondition: true),
        new(StepKind.Wait, "Ожидание времени", "Ожидание", "Пауза заданной длительности.",
            [new(P.Ms, "Длительность, мс", ParamType.Number, true, "1000")]),
        new(StepKind.Repeat, "Повтор", "Логика", "Повторяет вложенные блоки заданное число раз (ограничено настройками). Можно указать условие выхода.",
            [new(P.Count, "Количество повторов", ParamType.Number, true, "3")], IsContainer: true, HasCondition: true),
        new(StepKind.RunTemplate, "Запуск другого шаблона", "Логика", "Выполняет другой шаблон. Циклические вызовы блокируются.",
            [new(P.Template, "Шаблон", ParamType.Template, true)]),
        new(StepKind.EndTemplate, "Завершение шаблона", "Логика", "Досрочно завершает текущий шаблон.", []),
    ];

    public static readonly IReadOnlyList<ConditionDescriptor> Conditions =
    [
        new(ConditionKind.WindowExists, "Окно программы существует", [new(P.App, "Программа", ParamType.App), new(P.Title, "или часть заголовка", ParamType.Text)]),
        new(ConditionKind.WindowActive, "Окно программы активно", [new(P.App, "Программа", ParamType.App), new(P.Title, "или часть заголовка", ParamType.Text)]),
        new(ConditionKind.TextOnScreen, "Текст найден на экране", [new(P.Text, "Текст", ParamType.Text, true), new(P.App, "В окне программы", ParamType.App)]),
        new(ConditionKind.ElementExists, "Кнопка / элемент обнаружен", [new(P.App, "Программа", ParamType.App), new(P.Element, "Название элемента", ParamType.Text),
            new(P.AutomationId, "AutomationId", ParamType.Text), new(P.Anchor, "Запомненный элемент", ParamType.Anchor)]),
        new(ConditionKind.FileExists, "Файл существует", [new(P.Path, "Путь", ParamType.Path, true)]),
        new(ConditionKind.PreviousStepSucceeded, "Предыдущий шаг выполнен успешно", []),
        new(ConditionKind.TimeoutElapsed, "С начала шаблона прошло (мс)", [new(P.Ms, "Миллисекунды", ParamType.Number, true, "10000")]),
    ];

    public static StepDescriptor Get(StepKind kind) => All.First(d => d.Kind == kind);
    public static ConditionDescriptor Get(ConditionKind kind) => Conditions.First(d => d.Kind == kind);

    public static TemplateStep Create(StepKind kind)
    {
        var d = Get(kind);
        var s = new TemplateStep { Kind = kind };
        foreach (var p in d.Parameters)
            if (p.Default is not null) s.Parameters[p.Key] = p.Default;
        if (d.HasCondition && kind == StepKind.If)
            s.Condition = new StepCondition { Kind = ConditionKind.WindowExists };
        return s;
    }

    /// <summary>Встроенная категория опасности шага (без учёта пометки пользователя).</summary>
    public static DangerCategory GetDangerCategory(TemplateStep step, SecurityPolicy policy)
    {
        if (step.MarkedDangerous is { } marked && marked != DangerCategory.None) return marked;
        switch (step.Kind)
        {
            case StepKind.DeleteFile:
                return DangerCategory.DeleteFiles;
            case StepKind.SystemOperation:
                return Enum.TryParse<SystemOperation>(step.Get(P.Operation), true, out var op) ? op switch
                {
                    SystemOperation.Shutdown or SystemOperation.Restart or SystemOperation.Sleep => DangerCategory.PowerOff,
                    SystemOperation.EmptyRecycleBin => DangerCategory.DeleteFiles,
                    _ => DangerCategory.None,
                } : DangerCategory.None;
            case StepKind.PressKeys:
                return policy.ClassifyHotkey(step.Get(P.Keys) ?? "");
            case StepKind.CreateFile:
                return step.GetBool(P.Overwrite) ? DangerCategory.DeleteFiles : DangerCategory.None;
            case StepKind.LaunchApp:
            case StepKind.OpenUrl:
            {
                // Запуск скриптов и установщиков по пути.
                var target = (step.Get(P.App) ?? step.Get(P.Url) ?? "").ToLowerInvariant();
                if (target.EndsWith(".bat") || target.EndsWith(".cmd") || target.EndsWith(".ps1") || target.EndsWith(".vbs") || target.EndsWith(".js"))
                    return DangerCategory.RunScripts;
                if (target.EndsWith(".msi") || target.Contains("setup") || target.Contains("install"))
                    return DangerCategory.InstallSoftware;
                return DangerCategory.None;
            }
            default:
                return DangerCategory.None;
        }
    }

    /// <summary>Краткое описание шага для списка и голосовых сообщений.</summary>
    public static string Describe(TemplateStep s)
    {
        string? a = s.Get(P.App);
        return s.Kind switch
        {
            StepKind.LaunchApp => $"Запуск: {a ?? "?"}",
            StepKind.SwitchToWindow => $"Переключиться на: {a ?? s.Get(P.Title) ?? "?"}",
            StepKind.CloseWindow => $"Закрыть: {a ?? "активное окно"}",
            StepKind.MinimizeWindow => $"Свернуть: {a ?? "активное окно"}",
            StepKind.MaximizeWindow => $"Развернуть: {a ?? "активное окно"}",
            StepKind.WaitForApp => $"Ждать программу: {a ?? "?"} ({s.GetInt(P.TimeoutMs, 10000) / 1000.0:0.#} с)",
            StepKind.WaitForElement => $"Ждать элемент: {s.Get(P.Element) ?? s.Get(P.AutomationId) ?? "привязка"}",
            StepKind.FindText => $"Найти текст: «{s.Get(P.Text)}»",
            StepKind.ClickElement => $"Нажать: {s.Get(P.Element) ?? s.Get(P.AutomationId) ?? "привязка"}",
            StepKind.Mouse => $"Мышь: {s.Get(P.Action)} ({s.Get(P.X)}, {s.Get(P.Y)})",
            StepKind.TypeText => $"Ввести текст: «{Trim(s.Get(P.Text))}»",
            StepKind.PressKeys => $"Клавиши: {s.Get(P.Keys)}",
            StepKind.OpenUrl => $"Открыть сайт: {s.Get(P.Url)}",
            StepKind.CreateFile => $"Создать файл: {s.Get(P.Path)}",
            StepKind.MoveFile => $"Переместить: {s.Get(P.Source)} → {s.Get(P.Destination)}",
            StepKind.DeleteFile => $"Удалить в корзину: {s.Get(P.Path)}",
            StepKind.SetVolume => $"Громкость: {s.Get(P.Mode)} {s.Get(P.Value)}",
            StepKind.SystemOperation => $"Система: {OptionLabel(StepKind.SystemOperation, P.Operation, s.Get(P.Operation))}",
            StepKind.Say => $"Сказать: «{Trim(s.Get(P.Text))}»",
            StepKind.If => $"Если {DescribeCondition(s.Condition)}",
            StepKind.Wait => $"Ждать {s.GetInt(P.Ms, 1000) / 1000.0:0.##} с",
            StepKind.Repeat => $"Повторить {s.GetInt(P.Count, 1)} раз" + (s.Condition is null ? "" : $", пока не {DescribeCondition(s.Condition)}"),
            StepKind.RunTemplate => $"Шаблон: {s.Get(P.Template)}",
            StepKind.EndTemplate => "Завершить шаблон",
            _ => s.Kind.ToString(),
        };
    }

    public static string DescribeCondition(StepCondition? c)
    {
        if (c is null) return "(условие не задано)";
        var d = Get(c.Kind);
        var arg = c.Parameters.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var text = arg is null ? d.Title.ToLowerInvariant() : $"{d.Title.ToLowerInvariant()}: {arg}";
        return c.Negate ? "НЕ " + text : text;
    }

    private static string OptionLabel(StepKind kind, string key, string? value)
    {
        var p = Get(kind).Parameters.FirstOrDefault(x => x.Key == key);
        return p?.Options?.FirstOrDefault(o => o.Value == value).Label ?? value ?? "?";
    }

    private static string Trim(string? s) => s is null ? "" : s.Length > 40 ? s[..40] + "…" : s;
}
