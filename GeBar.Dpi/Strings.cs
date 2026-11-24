using System.Globalization;

namespace GeBar.Dpi;

internal static class Strings
{
    public static string Power => Ru ? "Питание" : "Power";
    public static string Sleep => Ru ? "Время до сна" : "Sleep after";
    public static string Performance => Ru ? "Режим сенсора" : "Sensor mode";
    public static string Normal => Ru ? "Обычный" : "Normal";
    public static string PowerHint(bool turbo) => Ru ? (turbo ? "Speed и Turbo повышают расход энергии." : "Speed повышает расход энергии.")
        : (turbo ? "Speed and Turbo increase power use." : "Speed increases power use.");
    public static string PowerUnavailable => Ru ? "Настройки питания этой мыши пока недоступны." : "Power settings for this mouse are not available yet.";
    public static string EnableSmartSaver => Ru ? "Включить" : "Enable";
    public static string NotifyGame => Ru ? "Уведомлять при входе в игру" : "Notify when entering a game";
    public static string SmartSaverHint => Ru ? "При обычной работе частота снижается до 125 Гц. В полноэкранных приложениях используется выбранная частота опроса. Работает и во время зарядки." : "Polling drops to 125 Hz on the desktop. Fullscreen applications use your selected polling rate. Also works while charging.";
    public static string FullscreenHint => Ru ? "Браузеры и известные видеоплееры исключены. После выхода из полного экрана экономия включается через 3 секунды." : "Browsers and known video players are excluded. Saving resumes 3 seconds after leaving fullscreen.";
    public static string SmartSaverOff => Ru ? "SmartSaver выключен" : "SmartSaver is off";
    public static string SmartSaverUnsupported => Ru ? "SmartSaver недоступен для этой мыши." : "SmartSaver is not available for this mouse.";
    public static string Saving => Ru ? "Экономия: 125 Гц" : "Saving: 125 Hz";
    public static string GameRate(int rate) => Ru ? $"Полный экран: {rate} Гц" : $"Fullscreen: {rate} Hz";
    public static string GameNotification(int rate) => Ru ? $"SmartSaver: игровая частота {rate} Гц" : $"SmartSaver: game polling rate {rate} Hz";
    public static string SleepDuration(int seconds) => seconds % 60 == 0 ? (Ru ? $"{seconds / 60} мин" : $"{seconds / 60} min") : (Ru ? $"{seconds} с" : $"{seconds} s");
    private static bool Ru => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
    public static string Title => Ru ? "Настройки мыши" : "Mouse settings";
    public static string Polling => Ru ? "Частота опроса" : "Polling rate";
    public static string Lod => Ru ? "Высота отрыва (LOD)" : "Lift-off distance (LOD)";
    public static string Read => Ru ? "Обновить" : "Refresh";
    public static string Loading => Ru ? "Чтение настроек…" : "Reading settings…";
    public static string Hint => Ru ? "Изменения записываются в мышь кнопкой «Сохранить»." : "Click Save to write changes to the mouse.";
    public static string Supported => Ru ? "Настройки этого устройства доступны через g-eDPI." : "This device supports settings through g-eDPI.";
    public static string Unsupported => Ru ? "Это устройство пока не поддерживает настройки через g-eDPI." : "This device does not support settings through g-eDPI yet.";
    public static string Unavailable => Ru ? "Мышь не ответила. Разбудите её и обновите настройки." : "The mouse did not respond. Wake it and refresh the settings.";
    public static string InterfaceUnavailable => Ru ? "Не найден доступный интерфейс настроек мыши." : "No accessible mouse settings interface was found.";
    public static string InvalidResponse => Ru ? "Мышь вернула неизвестный формат настроек." : "The mouse returned an unknown settings format.";
    public static string ChangedExternally => Ru ? "Настройки мыши изменились. Обновите их перед сохранением." : "Mouse settings have changed. Refresh them before saving.";
    public static string VerificationFailed => Ru ? "Не все настройки подтвердились. Обновите значения перед повторным сохранением." : "Some settings could not be verified. Refresh values before saving again.";
    public static string DpiRange(int minimum, int maximum, int step) => Ru ? $"От {minimum} до {maximum}, шаг {step}" : $"{minimum}-{maximum}, step {step}";
}
