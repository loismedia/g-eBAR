using System.Globalization;

namespace GeBar.HidApp;

internal static class Strings
{
    private static readonly bool Ru = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);

    public static string AppName => "g-eHID";
    public static string AboutTitle => Ru ? "О программе" : "About";
    public static string AboutText => Ru
        ? "Модуль находит HID-устройства, подбирает протокол чтения заряда и сохраняет профиль в g-eBAR. Захват пакетов и поиск байта зарядки нужны для устройств, у которых протокол не определился сам."
        : "Module discovers HID devices, resolves a battery protocol and saves the profile to g-eBAR. Packet capture and the charging byte search are for devices whose protocol was not resolved automatically.";
    public static string Version(string value) => Ru ? $"Версия {value}" : $"Version {value}";
    public static string Developer => "dev by lucz@lois.media";
    public static string Close => Ru ? "Закрыть" : "Close";
    public static string Cancel => Ru ? "Отмена" : "Cancel";
    public static string DownloadGeBar => Ru ? "Скачать g-eBAR" : "Download g-eBAR";
    public static string GeBarRequiredTitle => Ru ? "Нужен g-eBAR" : "g-eBAR is required";
    public static string GeBarRequiredText => Ru
        ? "g-eHID это модуль g-eBAR, сам по себе он ничего не отслеживает и сохраняет профили в папку g-eBAR. Установите g-eBAR и запустите модуль из него."
        : "g-eHID is a g-eBAR module and does not monitor anything on its own: it saves profiles into the g-eBAR folder. Install g-eBAR and launch the module from there.";
    public static string RefreshDevices => Ru ? "Обновить устройства" : "Refresh devices";
    public static string FoundDevices => Ru ? "Найденные устройства" : "Discovered devices";
    public static string SelectDevice => Ru ? "Выберите устройство" : "Select a device";
    public static string Export => Ru ? "JSON" : "JSON";
    public static string MergedDevices(string list) => Ru ? $"Объединено: {list}" : $"Merged: {list}";
    public static string UnknownVendor => Ru ? "без производителя" : "no vendor";
    public static string BuiltIn => Ru ? "встроенный" : "built-in";
    public static string AddToGeBar => Ru ? "Добавить в g-eBAR" : "Add to g-eBAR";
    public static string RemoveFromGeBar => Ru ? "Удалить из g-eBAR" : "Remove from g-eBAR";
    public static string UpdateInGeBar => Ru ? "Обновить в g-eBAR" : "Update in g-eBAR";
    public static string MergeProfiles => Ru ? "Объединить" : "Merge";
    public static string ProfileLabelHint => Ru ? "Пользовательское название профиля" : "Custom profile label";
    public static string ProfilesMerged => Ru ? "Подключения объединены в один профиль." : "Connections merged into one profile.";
    public static string HidInterfaces => Ru ? "HID-интерфейсы" : "HID interfaces";
    public static string Packets => Ru ? "Пакеты RX / TX" : "RX / TX packets";
    public static string NotRunning => Ru ? "не запущен" : "not running";
    public static string StartCapture => Ru ? "Начать захват" : "Start capture";
    public static string StopCapture => Ru ? "Остановить" : "Stop";
    public static string Capturing => Ru ? "идёт захват" : "capturing";
    public static string CaptureUnavailable => Ru ? "недоступен" : "unavailable";
    public static string DetectBattery => Ru ? "Определить заряд" : "Detect battery";
    public static string FindCharging => Ru ? "Найти зарядку" : "Find charging";
    public static string RememberOff => Ru ? "Найти OFF" : "Find OFF";
    public static string DeviceOffline => Ru ? "сейчас не на связи" : "not connected right now";
    public static string SleepWatchTitle => Ru ? "Поиск признака сна" : "Sleep marker search";
    public static string SleepWatchText => Ru
        ? "Укажите, через сколько секунд простоя устройство засыпает. Это значение есть в его настройках или в описании. После запуска не трогайте устройство и не двигайте мышь: g-eHID будет слушать его пакеты всё это время и сравнит, что приходит до засыпания и после. Затем в строке состояния появится просьба разбудить устройство: признак сна засчитывается только если после пробуждения он больше не приходит. Наблюдение продлится на минуту дольше указанного времени."
        : "Enter how many idle seconds the device needs to fall asleep. The value is in its settings or its description. After the start do not touch the device: g-eHID listens to its packets for the whole time and compares what arrives before and after it sleeps. Then the status line asks you to wake the device: a sleep marker counts only if it stops arriving once the device is awake. The watch runs a minute longer than the time you enter.";
    public static string SleepSeconds => Ru ? "Секунд до сна:" : "Seconds until sleep:";
    public static string SleepSecondsRange(int minimum, int maximum) => Ru
        ? $"Введите число от {minimum} до {maximum} секунд."
        : $"Enter a number between {minimum} and {maximum} seconds.";
    public static string SleepWatchStart => Ru ? "Начать наблюдение" : "Start the watch";
    public static string StopSleepWatch => Ru ? "Остановить" : "Stop";
    public static string SleepWatchStopped => Ru ? "Наблюдение остановлено." : "The watch was stopped.";
    public static string SleepWatchIdle(int seconds, int packets) => Ru
        ? $"Ждём засыпания: {seconds} с, пакетов {packets}"
        : $"Waiting for the sleep: {seconds} s, packets {packets}";
    public static string SleepWatchListening(int seconds, int packets) => Ru
        ? $"Ищем признак сна: {seconds} с, пакетов {packets}"
        : $"Looking for the sleep marker: {seconds} s, packets {packets}";
    public static string SleepWatchWake(int seconds, int packets) => Ru
        ? $"Разбудите устройство: {seconds} с, пакетов {packets}"
        : $"Wake the device: {seconds} s, packets {packets}";
    public static string SleepWatchSilent => Ru
        ? "Устройство ничего не прислало при засыпании, отдельного признака сна у него нет."
        : "The device sent nothing when falling asleep, so it has no separate sleep marker.";
    public static string SleepWatchAmbiguous(int count) => Ru
        ? $"После засыпания пришло разных пакетов: {count}. Однозначно выбрать нельзя, повторите наблюдение, не трогая устройство."
        : $"Different packets after the sleep: {count}. The marker cannot be identified, repeat the watch without touching the device.";
    public static string BatteryFoundOnConnections(int count) => Ru
        ? $"Заряд определён по всем соединениям, которые сейчас на связи: {count}. Профиль знает протокол для каждого из них."
        : $"Battery read on every connection that is live right now: {count}. The profile now knows a protocol for each of them.";
    public static string SleepWatchNoInterface => Ru ? "Не удалось открыть ни один интерфейс устройства для прослушивания." : "None of the device interfaces could be opened for listening.";
    public static string OffPacketSaved(string prefix) => Ru
        ? $"OFF-пакет сохранён ({prefix}). Интерфейс состояния добавлен в профиль."
        : $"OFF packet saved ({prefix}). The status interface was added to the profile.";
    public static string ChargingStepOne => Ru
        ? "Снимок без зарядки снят. Подключите кабель и нажмите «Найти зарядку» ещё раз."
        : "Snapshot without charging taken. Connect the cable and press “Find charging” again.";
    public static string ChargingNeedsProtocol => Ru
        ? "Сначала нажмите «Определить заряд», нужен рабочий протокол."
        : "Press “Detect battery” first, a working protocol is required.";
    public static string ChargingUnsupported => Ru
        ? "Ответ устройства не изменился при подключении кабеля, отдельного признака зарядки у него нет. g-eBAR определит её по росту заряда."
        : "The device answer did not change when the cable was connected, so it has no charging flag. g-eBAR will infer charging from a rising battery.";
    public static string ChargingNotFound => Ru
        ? "Байт зарядки не найден: ответ не изменился. Убедитесь, что кабель действительно переключён."
        : "Charging byte not found: the response did not change. Make sure the cable was actually toggled.";
    public static string ChargingAmbiguous(int count) => Ru
        ? $"Изменилось байт: {count}. Однозначно определить зарядку нельзя, повторите, не трогая устройство."
        : $"Bytes changed: {count}. Charging cannot be identified, retry without touching the device.";
    public static string ChargingFound(int offset, int value) => Ru
        ? $"Зарядка найдена: байт {offset}, значение {value}. Профиль обновлён."
        : $"Charging found: byte {offset}, value {value}. Profile updated.";
    public static string Clear => Ru ? "Очистить" : "Clear";
    public static string PassiveScanHint => Ru ? "Пассивное сканирование не отправляет команды устройствам." : "Passive scanning does not send commands to devices.";
    public static string ModelsFound(int count) => Ru ? $"Найдено моделей: {count}. Интерфейсы сгруппированы по имени и VID/PID." : $"Models found: {count}. Interfaces are grouped by name and VID/PID.";
    public static string ScanError => Ru ? "Не удалось получить список HID-устройств." : "Could not read the HID device list.";
    public static string DeviceMetadata(int vid, int pid, string? manufacturer, int interfaces) => Ru ? $"VID={vid:X4}  PID={pid:X4}  Производитель: {manufacturer ?? "неизвестен"}  Интерфейсов: {interfaces}" : $"VID={vid:X4}  PID={pid:X4}  Manufacturer: {manufacturer ?? "unknown"}  Interfaces: {interfaces}";
    public static string InterfaceOpened => Ru ? "Интерфейс открыт, входящие пакеты пишутся в журнал." : "Interface opened, incoming packets are being logged.";
    public static string CheckingBattery => Ru ? "Проверяю стандартные HID battery usages и известные протоколы…" : "Checking standard HID battery usages and known protocols…";
    public static string BatteryFound(int value, bool? charging, string? detail) => Ru ? $"Найден заряд: {value}%{ChargeState(charging)} · {detail}. Профиль готов к экспорту." : $"Battery found: {value}%{ChargeState(charging)} · {detail}. The profile is ready to export.";
    public static string PassiveCaptureStarted => Ru ? "Стандартное поле заряда не ответило. Запущен пассивный захват первого доступного HID-интерфейса." : "The standard battery field did not respond. Passive capture started on the first available HID interface.";
    public static string NoInputInterface => Ru ? "Захват для этого устройства недоступен: интерфейсы заняты Windows или фирменной программой." : "Capture is unavailable for this device: the interfaces are held by Windows or a vendor application.";
    public static string ExportTitle => Ru ? "Экспорт профиля g-eHID" : "Export g-eHID profile";
    public static string JsonFilter => Ru ? "Профиль JSON (*.json)|*.json" : "JSON profile (*.json)|*.json";
    public static string ProfileExported(string path) => Ru ? $"Профиль экспортирован: {path}" : $"Profile exported: {path}";
    public static string ProfileAdded => Ru ? "Профиль добавлен в g-eBAR." : "Profile added to g-eBAR.";
    public static string ProfileWriteFailed => Ru ? "Не удалось сохранить профиль, проверьте доступ к папке g-eBAR." : "The profile could not be saved, check access to the g-eBAR folder.";
    public static string ProfileRemoved => Ru ? "Профиль удалён из g-eBAR." : "Profile removed from g-eBAR.";
    public static string ProfileUpdatedInGeBar => Ru ? "Профиль обновлён в g-eBAR." : "Profile updated in g-eBAR.";
    public static string ProfileAddedNotice => Ru ? "Заготовка профиля сохранена в g-eBAR. Она помечена как непроверенная и не будет использоваться для отправки команд, пока протокол устройства не настроен." : "The profile draft was saved to g-eBAR. It is marked as unverified and will not send commands until the device protocol is configured.";
    public static string SweepCommands => Ru ? "Перебор команд" : "Sweep commands";
    public static string StopSweep => Ru ? "Остановить перебор" : "Stop sweep";
    public static string SweepWarning => Ru
        ? "Перебор отправит устройству все 256 команд вендорского кадра!"
        : "The sweep sends the device all 256 commands of the vendor frame!";
    public static string SweepFinished(int count) => Ru ? $"Перебор завершён. Ответов: {count}." : $"Sweep finished. Answers: {count}.";
    public static string SweepStopped => Ru ? "Перебор остановлен." : "Sweep stopped.";
    public static string SweepUnavailable => Ru ? "У устройства нет вендорского интерфейса 65/65, перебирать нечего." : "The device has no 65/65 vendor interface, nothing to sweep.";

    public static string InterfacePath(int index, string path) => Ru ? $"Интерфейс {index} · {path}" : $"Interface {index} · {path}";

    private static string ChargeState(bool? charging) => charging is null ? string.Empty : charging.Value ? Ru ? " · заряжается" : " · charging" : Ru ? " · разряжается" : " · discharging";
}
