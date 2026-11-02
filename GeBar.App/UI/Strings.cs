using GeBar.Core;
using System.Globalization;

namespace GeBar.App;

internal static class Strings
{
    private static readonly bool Ru = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);
    public static string DefaultDevice => Ru ? "Беспроводное устройство" : "Wireless device";
    public static string AppName => "g-eBAR";
    public static string HidName => "g-eHID";
    public static string Modules => Ru ? "Модули" : "Modules";
    public static string RemoveHid => Ru ? "Удалить g-eHID" : "Remove g-eHID";
    public static string HidVersion(string value) => $"g-eHID {value}";
    public static string HidReleaseUnavailable => Ru ? "g-eHID недоступен в этом релизе" : "g-eHID is unavailable in this release";
    public static string HidReleaseVersion(string value) => $"g-eHID {value}";
    public static string HidAvailableVersion(string value) => Ru ? $"Доступна версия g-eHID: {value}" : $"g-eHID version available: {value}";
    public static string HidInstalled => Ru ? "g-eHID установлен." : "g-eHID has been installed.";
    public static string HidInstallHint => Ru ? "g-eHID не установлен. Откройте «Обновление» и отметьте g-eHID." : "g-eHID is not installed. Open Check for updates and select g-eHID.";
    public static string HidRemoveError => Ru ? "Не удалось удалить g-eHID." : "Could not remove g-eHID.";
    public static string EmptyValue => "—";
    public static string Saving => Ru ? "Сохранение…" : "Saving…";
    public static string Saved => Ru ? "Сохранено" : "Saved";
    public static string Charge => Ru ? "Заряд" : "Battery";
    public static string State => Ru ? "Состояние" : "Status";
    public static string Refresh => Ru ? "Рефреш" : "Refresh";
    public static string Parameters => Ru ? "Параметры" : "Settings";
    public static string OpenLog => Ru ? "Логи" : "Logs";
    public static string Home => Ru ? "Главная" : "Home";
    public static string ControlCenter => Ru ? "Консоль управления" : "Control center";
    public static string DeviceStatus => Ru ? "Состояние устройства" : "Device status";
    public static string Monitoring => Ru ? "Мониторинг" : "Monitoring";
    public static string Notifications => Ru ? "Уведомления" : "Notifications";
    public static string TrayAppearance => Ru ? "Значок и цвета батареи" : "Tray icon and battery colors";
    public static string CheckUpdates => Ru ? "Обновление" : "Check for updates";
    public static string BugDescription => Ru ? "Краткое описание проблемы" : "Briefly describe the bug";
    public static string BatteryHistory => Ru ? "История заряда" : "Battery history";
    public static string NoHistory => Ru ? "Пока нет данных, график появится после нескольких замеров." : "No data yet, the chart appears after a few readings.";
    public static string HistoryDay => Ru ? "24 часа" : "24 hours";
    public static string HistoryWeek => Ru ? "7 дней" : "7 days";
    public static string HistoryCoarse(int seconds) => Ru
        ? $"Интервал обновления {Minutes(seconds)}. График и прогноз могут быть неточны, короткие сессии зарядки не видны. Рекомендуется не больше 5 минут."
        : $"Refresh interval is {Minutes(seconds)}. The chart and estimate may be inaccurate and short charging sessions are missed. Keep it at 5 minutes or less.";
    private static string Minutes(int seconds) => seconds >= 3600
        ? Ru ? $"{seconds / 3600} ч" : $"{seconds / 3600} h"
        : Ru ? $"{seconds / 60} мин" : $"{seconds / 60} min";
    public static string HistoryRangeHint => Ru ? "Период графика" : "Chart range";
    public static string ChargingStarted => Ru ? "Зарядка подключена" : "Charging connected";
    public static string OfflineCharge(int from, int to, DateTime time) => Ru
        ? $"Заряжено без записи: {from}% → {to}% ({time:dd.MM HH:mm})"
        : $"Charged while not tracked: {from}% → {to}% ({time:dd.MM HH:mm})";
    public static string ChargingStopped => Ru ? "Зарядка отключена" : "Charging disconnected";
    public static string RemainingEstimate(TimeSpan value) => value.TotalHours >= 24
        ? Ru ? $"осталось ≈ {(int)value.TotalDays} д {value.Hours} ч" : $"≈ {(int)value.TotalDays} d {value.Hours} h left"
        : Ru ? $"осталось ≈ {(int)value.TotalHours} ч {value.Minutes} мин" : $"≈ {(int)value.TotalHours} h {value.Minutes} min left";
    public static string ReportBug => Ru ? "Зарепортить багу" : "Report bug";
    public static string BugDescriptionRequired => Ru ? "Сначала опишите ошибку." : "Describe the bug first.";
    public static string BugReportingUnavailable => Ru ? "Адрес GitHub-репозитория отсутствует в этой сборке. Отчёт скопирован в буфер обмена." : "This build has no GitHub repository address. The report was copied to the clipboard.";
    public static string NewVersion => Ru ? "Доступна новая версия" : "New version available";
    public static string CurrentVersion(string value) => Ru ? $"Текущая версия: {value}" : $"Current version: {value}";
    public static string AvailableVersion(string value) => Ru ? $"Доступна версия: {value}" : $"Available version: {value}";
    public static string Download => Ru ? "Установить обновление" : "Install update";
    public static string AvailableNightlyVersion(string version) => Ru ? $"Доступна версия {version} nightly" : $"Version {version} nightly is available";
    public static string AvailableStableRollback(string version) => Ru ? $"Доступен откат на стабильную версию {version}" : $"Stable rollback {version} is available";
    public static string NightlyWarning => Ru ? "Nightly-сборка предназначена для дополнительного тестирования и может работать нестабильно." : "This nightly build is intended for additional testing and may be unstable.";
    public static string StableRollbackWarning => Ru ? "Вы используете nightly-сборку. Новых версий нет, но можно вернуться на последнюю стабильную версию." : "You are running a nightly build. No newer version is available, but you can return to the latest stable release.";
    public static string NightlyConfirmationTitle => Ru ? "Установить nightly-сборку?" : "Install nightly build?";
    public static string NightlyConfirmation => Ru ? "Это тестовая nightly-сборка. Она может содержать ошибки, работать нестабильно или потребовать отката. Всё равно установить обновление?" : "This is a test nightly build. It may contain bugs, be unstable, or require a rollback. Install the update anyway?";
    public static string StableRollbackConfirmationTitle => Ru ? "Вернуться на стабильную версию?" : "Return to stable version?";
    public static string StableRollbackConfirmation => Ru ? "Будет установлена более ранняя стабильная версия. Настройки, созданные новой nightly-сборкой, могут быть с ней несовместимы. Продолжить откат?" : "An earlier stable version will be installed. Settings created by the newer nightly build may be incompatible with it. Continue with the rollback?";
    public static string Rollback => Ru ? "Откатиться" : "Roll back";
    public static string DownloadStarting => Ru ? "Подготовка загрузки…" : "Preparing download…";
    public static string DownloadProgress(long downloaded, long? total) => total is > 0
        ? Ru ? $"Загрузка: {downloaded / 1048576d:F1} из {total.Value / 1048576d:F1} МБ" : $"Downloading: {downloaded / 1048576d:F1} of {total.Value / 1048576d:F1} MB"
        : Ru ? $"Загружено: {downloaded / 1048576d:F1} МБ" : $"Downloaded: {downloaded / 1048576d:F1} MB";
    public static string VersionHistory => Ru ? "Прошлые версии" : "Previous versions";
    public static string ChangelogUnavailable => Ru ? "Не удалось загрузить историю изменений." : "Could not load version history.";
    public static string Downloaded => Ru ? "Обновление загружено. Приложение будет перезапущено." : "The update has been downloaded. The application will restart.";
    public static string NoUpdates => Ru ? "Установлена последняя версия." : "You have the latest version.";
    public static string UpdateInstallError => Ru ? "Не удалось установить обновление." : "Could not install the update.";
    public static string HidLaunchError => Ru ? "Не удалось запустить g-eHID." : "Could not launch g-eHID.";
    public static string UpdateUnavailable => Ru ? "Не удалось проверить обновления." : "Could not check for updates.";
    public static string Exit => Ru ? "Выход" : "Exit";
    public static string Updated => Ru ? "Обновлено" : "Updated";
    public static string Never => Ru ? "никогда" : "never";
    public static string Save => Ru ? "Сохранить" : "Save";
    public static string Cancel => Ru ? "Отмена" : "Cancel";
    public static string ChooseColor => Ru ? "Выбор цвета" : "Choose color";
    public static string HexColor => Ru ? "Цвет HEX" : "HEX color";
    public static string InvalidColor => Ru ? "Введите цвет в формате #RRGGBB." : "Enter a color in #RRGGBB format.";
    public static string WarningThresholdError => Ru ? "Порог предупреждения должен быть от 5 до 50%." : "Warning threshold must be between 5% and 50%.";
    public static string CriticalThresholdError => Ru ? "Критический порог должен быть от 1 до 49%." : "Critical threshold must be between 1% and 49%.";
    public static string ThresholdError => Ru ? "Критический порог должен быть ниже порога предупреждения." : "Critical threshold must be lower than warning threshold.";
    public static string IntervalError => Ru ? "Интервал обновления должен быть от 15 до 86400 секунд." : "Refresh interval must be between 15 and 86400 seconds.";
    public static string CriticalNotice(int value) => Ru ? $"Критический заряд: {value}%" : $"Critical battery: {value}%";
    public static string WarningNotice(int value) => Ru ? $"Низкий заряд: {value}%" : $"Low battery: {value}%";
    public static string ChargingNotice(int value) => Ru ? $"Зарядка подключена: {value}%" : $"Charging connected: {value}%";
    public static string FullNotice => Ru ? "Устройство полностью заряжено" : "Device fully charged";
    public static string NoDevicesError => Ru ? "Не удалось загрузить ни одного профиля устройства. Переустановите g-eBAR." : "No device profile could be loaded. Reinstall g-eBAR.";
    public static string SettingsSaveError => Ru ? "Не удалось сохранить настройки." : "Could not save settings.";
    public static string LogoMissing => Ru ? "Ресурс логотипа не найден." : "Logo resource is missing.";
    public static string SelectedInterface => Ru ? "Выбранный интерфейс" : "Selected interface";
    public static string Protocol => Ru ? "Протокол" : "Protocol";
    public static string TokenRejected => Ru
        ? "GitHub отклонил токен: он истёк или введён с ошибкой. Откройте «Параметры» и вставьте новый."
        : "GitHub rejected the token: it is expired or mistyped. Open Settings and paste a new one.";
    public static string TokenWithoutAccess => Ru
        ? "Токен принят, но доступа к репозиторию g-eBAR у него нет. Нужен токен аккаунта, которому открыт этот репозиторий."
        : "The token works, but it has no access to the g-eBAR repository. Use a token from an account that can read it.";
    public static string TokenMissing => Ru ? "GitHub-токен не задан. Откройте «Параметры» и вставьте токен в поле «Токен»." : "No GitHub token. Open Settings and paste a token into the Token field.";
    public static string TokenInvalid => Ru ? "Это не похоже на GitHub-токен. Ожидается ghp_… или github_pat_…" : "This does not look like a GitHub token. Expected ghp_… or github_pat_…";
    public static string LastError => Ru ? "Последняя ошибка" : "Last error";
    public static string Startup => Ru ? "Запускать вместе с Windows" : "Start with Windows";
    public static string NotifyLow => Ru ? "Уведомлять при низком заряде" : "Notify about low battery";
    public static string NotifyCharging => Ru ? "Уведомлять о подключении зарядки" : "Notify when charging is connected";
    public static string NotifyScope => Ru ? "Уведомления" : "Notifications";
    public static string NotifyScopeSelected => Ru ? "Для выбранного устройства" : "Selected device only";
    public static string NotifyScopeAll => Ru ? "Для всех устройств" : "All devices";
    public static string NotifyFull => Ru ? "Уведомлять о полном заряде" : "Notify when fully charged";
    public static string Warning => Ru ? "Порог предупреждения, %" : "Warning threshold, %";
    public static string Critical => Ru ? "Критический порог, %" : "Critical threshold, %";
    public static string Interval => Ru ? "Интервал обновления, секунд" : "Refresh interval, seconds";
    public static string Raw => Ru ? "Записывать raw packets" : "Log raw packets";
    public static string RawOffWarning => Ru ? "Для улучшения анализа бага рекомендуется включить запись raw packets." : "Logging raw packets is recommended for a better bug analysis.";
    public static string AutoSwitch => Ru ? "Автоматически выбирать доступное устройство, если выбранное отключено" : "Automatically select an available device when the selected one is disconnected";
    public static string AllDevicesTooltip => Ru ? "Показывать заряд всех устройств в трее" : "Show all device batteries in the tray tooltip";
    public static string GitHubAccess => Ru ? "Доступ к GitHub" : "GitHub access";
    public static string GitHubToken => Ru ? "Токен" : "Token";
    public static string SwitchDev => Ru ? "Перейти на dev" : "Switch to dev";
    public static string SwitchPublic => Ru ? "Вернуться на публичную версию" : "Return to public version";
    public static string ChannelAvailable(ReleaseChannel channel, string version) => Ru
        ? $"Переход на {(channel == ReleaseChannel.Dev ? "dev" : "публичную версию")} {version}"
        : $"Switch to {(channel == ReleaseChannel.Dev ? "dev" : "public")} {version}";
    public static string ChannelConfirmation(ReleaseChannel channel) => Ru
        ? $"Установить {(channel == ReleaseChannel.Dev ? "dev-сборку" : "публичную сборку")}? Настройки и профили сохранятся."
        : $"Install the {(channel == ReleaseChannel.Dev ? "dev" : "public")} build? Settings and profiles will be preserved.";
    public static string TokenSaveError => Ru ? "Не удалось сохранить GitHub token в Windows Credential Manager." : "Could not save the GitHub token in Windows Credential Manager.";
    public static string Personalization => Ru ? "Персонализация" : "Personalization";
    public static string About => Ru ? "О программе" : "About";
    public static string NormalColor => Ru ? "Обычный заряд" : "Normal battery";
    public static string WarningColor => Ru ? "Предупреждение" : "Warning";
    public static string CriticalColor => Ru ? "Критический заряд" : "Critical battery";
    public static string IconStyle => Ru ? "Значок в трее" : "Tray icon";
    public static string IconScale => Ru ? "Масштаб значка" : "Icon scale";
    public static string IconScaleSmall => Ru ? "Маленький" : "Small";
    public static string IconScaleDefault => Ru ? "Обычный" : "Default";
    public static string IconScaleLarge => Ru ? "Большой" : "Large";
    public static string BatteryIcon => Ru ? "Батарея" : "Battery";
    public static string PercentageIcon => Ru ? "Процент числом" : "Percentage";
    public static string Developer => "dev by lucz@lois.media";
    public static string Version(string value) => Ru ? $"Версия {value}" : $"Version {value}";
    public static string AboutText => Ru ? "Легкий монитор заряда беспроводных устройств" : "Light battery monitor for wireless devices";
    public static string Status(DeviceBatteryState state) => state.ConnectionState switch
    {
        DeviceConnectionState.DeviceNotFound => Ru ? "устройство не подключено" : "device not connected",
        DeviceConnectionState.DeviceOff when state.Percentage is not null => Ru ? "спит" : "asleep",
        DeviceConnectionState.DeviceOff => Ru ? "устройство выключено" : "device is off",
        DeviceConnectionState.DeviceConnected when state.IsCharging == true => (Ru ? "заряжается" : "charging") + BluetoothSuffix(state),
        DeviceConnectionState.DeviceConnected => (Ru ? "подключено" : "connected") + BluetoothSuffix(state),
        DeviceConnectionState.Reading => Ru ? "чтение…" : "reading…",
        _ => Ru ? "ошибка чтения" : "read error"
    };

    private static string BluetoothSuffix(DeviceBatteryState state) => state.Transport == ConnectionTransport.Bluetooth ? " / Bluetooth" : string.Empty;

    public static string DiagnosticsText(HidDiagnostics value) => $"{SelectedInterface}: {value.SelectedDevicePath ?? EmptyValue}{Environment.NewLine}{Protocol}: {value.ProtocolName ?? EmptyValue}{Environment.NewLine}RX: {value.LastRx ?? EmptyValue}{Environment.NewLine}{LastError}: {value.LastError ?? EmptyValue}";

    public static string TrayTooltip(DeviceBatteryState state) =>
        $"{state.DeviceName ?? DefaultDevice} / {Status(state)}{(state.Percentage is int value ? $", {value}%" : string.Empty)}";
}
