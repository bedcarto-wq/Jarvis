namespace Jarvis.Core.Security;

/// <summary>Категории потенциально опасных операций (редактируемый список в настройках).</summary>
public enum DangerCategory
{
    None = 0,
    DeleteFiles,
    FormatDisks,
    InstallSoftware,
    ChangeSystemSettings,
    RunAsAdministrator,
    RunScripts,
    PowerOff,
    SendMessages,
    PublishData,
    Purchases,
    Custom,
}

public static class DangerCategoryNames
{
    public static string ToRussian(this DangerCategory c) => c switch
    {
        DangerCategory.DeleteFiles => "Удаление файлов и папок",
        DangerCategory.FormatDisks => "Форматирование дисков",
        DangerCategory.InstallSoftware => "Установка и удаление программ",
        DangerCategory.ChangeSystemSettings => "Изменение системных настроек",
        DangerCategory.RunAsAdministrator => "Запуск от имени администратора",
        DangerCategory.RunScripts => "Выполнение произвольных скриптов",
        DangerCategory.PowerOff => "Выключение, перезагрузка или сон ПК",
        DangerCategory.SendMessages => "Отправка сообщений",
        DangerCategory.PublishData => "Публикация данных в интернете",
        DangerCategory.Purchases => "Покупки и финансовые действия",
        DangerCategory.Custom => "Помечено пользователем как опасное",
        _ => "Обычное действие",
    };
}
