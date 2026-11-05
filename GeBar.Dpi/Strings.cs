using System.Globalization;

namespace GeBar.Dpi;

internal static class Strings
{
    private static bool Ru => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
    public static string Title => Ru ? "Настройки мыши" : "Mouse settings";
    public static string Polling => Ru ? "Частота опроса" : "Polling rate";
    public static string Lod => Ru ? "Высота отрыва (LOD)" : "Lift-off distance (LOD)";
    public static string Read => Ru ? "Обновить" : "Refresh";
    public static string Loading => Ru ? "Чтение настроек…" : "Reading settings…";
    public static string Hint => Ru ? "Изменения записываются в мышь кнопкой «Сохранить»." : "Click Save to write changes to the mouse.";
    public static string Unsupported => Ru ? "Управление настройками этой мыши пока не поддерживается." : "Settings for this mouse are not supported yet.";
    public static string Unavailable => Ru ? "Мышь не ответила. Разбудите её и обновите настройки." : "The mouse did not respond. Wake it and refresh the settings.";
    public static string InterfaceUnavailable => Ru ? "Не найден доступный интерфейс настроек мыши." : "No accessible mouse settings interface was found.";
    public static string InvalidResponse => Ru ? "Мышь вернула неизвестный формат настроек." : "The mouse returned an unknown settings format.";
    public static string ChangedExternally => Ru ? "Настройки мыши изменились. Обновите их перед сохранением." : "Mouse settings have changed. Refresh them before saving.";
    public static string VerificationFailed => Ru ? "Не все настройки подтвердились. Обновите значения перед повторным сохранением." : "Some settings could not be verified. Refresh values before saving again.";
    public static string DpiRange(int minimum, int maximum, int step) => Ru ? $"От {minimum} до {maximum}, шаг {step}" : $"{minimum}–{maximum}, step {step}";
}
