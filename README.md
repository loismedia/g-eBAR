# g-eBAR

A wireless device battery monitor for Windows. Displays the selected device's battery level in the system tray.

**g-eHID** is the companion module: it discovers HID devices, identifies their battery protocol and creates profiles for g-eBAR.

| | |
|---|---|
| Version | 1.4.0 |
| Windows | Windows 11; Mica requires 22H2 (build 22621). Windows 10 has not been tested. |
| Framework | `net10.0-windows` |
| Interface | WPF; native tray icon and menu through `Shell_NotifyIcon` |
| Dependencies | .NET 10, HidSharp 2.6.4; Bluetooth through SetupAPI |

## Getting started

1. Download `g-eBAR.exe` from [Releases](https://github.com/loismedia/g-eBAR/releases/latest), place it in a permanent writable folder and run it. The release includes the .NET runtime.
2. Double-click the tray icon to open the control center. The built-in profile is for Logitech G PRO X 2.
3. To add another device, open the update window and install g-eHID. In g-eHID, select your device, click **Detect battery**, then **Add to g-eBAR**.
4. Return to g-eBAR, refresh the device list and select your device. Profiles are also reloaded automatically.

## Device support

| Battery source | Devices | Status |
|---|---|---|
| Logitech Gaming `0x51` | LIGHTSPEED receivers | Tested |
| Feature `0x83` | WLMOUSE HUAN 8K and compatible devices | Tested |
| FEA status `0xF7` | RY5088 receivers: MonsGeek, Akko, AJAZZ | Tested on MonsGeek; byte 4 indicates sleep, not charging |
| Compx report 8 `0x04` | VGN and compatible devices | Tested |
| Waizowl `0x41` | OGM V2 Pro, OGM 8K through a receiver | Tested on a tester's device |
| Waizowl `0x01` | Wired OGM | Implemented from tester logs; battery packets use the `8C 01 00` marker |
| Beast X `0x04/0x1A` | WLMOUSE Beast X | Implemented, not tested on hardware |
| HID++ 2.0 `0x1000` | Logitech | Implemented, not tested on hardware |
| HID usages `0x06:0x20`, `0x85` | Bluetooth devices and controllers | Implemented, not tested on hardware |
| Windows PnP | Bluetooth devices whose battery level is available to Windows | Matches names from the profile; battery readings have not been confirmed on hardware |
| 33-byte Input/Output receivers | `36B0:3002` and similar devices | Not supported yet |

Protocols are matched by Input, Output and Feature report sizes. Vendor IDs only affect the order of probing. Commands are sent to vendor collections (`UsagePage >= 0xFF00`); standard keyboard and mouse collections are excluded. Responses are checked for empty data, echo signatures and consistent repeated readings.

If a stored protocol stops responding, g-eBAR tries the known protocols again. After three unsuccessful scans, full probing pauses for 30 minutes while the stored request continues to be tried. A change in connected interfaces resets the pause. Older profiles are normalized against the protocol catalog without requiring re-creation.

## Charging and sleep

An explicit charging flag from the device takes priority. Without one, g-eBAR estimates charging from battery changes:

| Observation | Interpretation |
|---|---|
| Rise of at least 5 percentage points between readings | Cable connected |
| Drop of at least 4 percentage points between readings | Cable disconnected |
| Rise of 3 percentage points within 10 minutes | Charging |
| Trend above +1.5 percentage points per hour | Slow charging |
| Trend below −1.5 percentage points per hour, or no rise for 30 minutes | Charging ended |

In g-eHID, **Find charging** compares packets captured with the cable disconnected and connected to identify a charging byte.

Sleeping devices retain their last known percentage. MonsGeek receivers can report a cached level during sleep; a Logitech receiver's `05 00 FF` response is also treated as sleep. The first reading after sleep does not count as a cable-related battery jump.

## Multiple devices

The device list combines the built-in profile with `%LocalAppData%\g-eBAR\Profiles\*.json`. A user profile with the same `Id` overrides the built-in profile. Use g-eHID to rename profiles or merge a device's wired and wireless connections.

When the selected device disappears, g-eBAR selects the first available device in profile order. The tray, control center and saved selection change together. A sleeping device with a known percentage stays selected. Automatic switching starts with a separate check 30 seconds after launch. A previously selected device returning does not change the current selection.

## History and remaining time

The home page shows battery history for 24 hours or seven days. Charging connections and disconnections are marked on the graph.

Remaining time is estimated from up to seven days of readings. Gaps longer than 30 minutes are excluded from both elapsed time and battery loss. At least a 3% drop and 30 minutes of data are required. Polling intervals longer than five minutes display a warning because short charging sessions may be missed.

## Configuration

Settings are stored in `%LocalAppData%\g-eBAR\settings.json`.

| Setting | Default | Description |
|---|---|---|
| `SelectedDeviceId` | `built-in` | Selected device |
| `StartWithWindows` | `false` | Start with Windows |
| `RefreshIntervalSeconds` | `60` | Polling interval, 15–86400 seconds |
| `HistoryHours` | `24` | Graph period: `24` or `168` hours |
| `NotifyLowBattery` | `true` | Low battery notifications |
| `NotifyChargingConnected` | `true` | Charging started notifications |
| `NotifyFullyCharged` | `true` | Fully charged notifications |
| `ShowAllDevicesInTrayTooltip` | `false` | Show all battery levels in the tray tooltip |
| `NotifyAllDevices` | `false` | Notify for all monitored devices |
| `AutoSwitchDevice` | `true` | Select an available device when the current one disconnects |
| `WarningThreshold` | `20` | Warning level, 5–50% |
| `CriticalThreshold` | `10` | Critical level, 1–49%, below the warning level |
| `IconStyle` | `Battery` | `Battery` or `Percentage` |
| `TrayIconScale` | `2` | Small (`1`), normal (`2`) or large (`3`) |
| `NormalColorArgb` | `#00BCD4` | Normal battery color, stored as an ARGB integer |
| `WarningColorArgb` | `#FFA500` | Warning color, stored as an ARGB integer |
| `CriticalColorArgb` | `#FF0000` | Critical color, stored as an ARGB integer |
| `LogRawPackets` | `false` | Include raw HID packets in logs |

Out-of-range values are clamped when loaded. Public and dev share settings and profiles. Extra developer data lives in `dev` objects and is preserved by public builds, along with unknown JSON fields. Profiles use shared format version 2.

Public updates do not require a GitHub token. To access dev, hold Ctrl and double-click the version on the About page, enter a token with read access to the dev repository and select **Switch to dev**. Dev provides **Return to public version** without requiring a token. Switching preserves data and replaces an installed g-eHID module with its counterpart from the target channel. Tokens are stored in Windows Credential Manager as `g-eBAR/GitHub`, never in configuration files.

## Diagnostics

Logs are stored in `%LocalAppData%\g-eBAR\Logs`, limited to 1 MB per file and 10 MB in total. The bug button on the About page opens a GitHub issue with device state, protocol and build details. Copies of today's g-eBAR and g-eHID logs are prepared for manual attachment.

| Option | Purpose |
|---|---|
| `--diagnostics` | Open the control center with diagnostics instead of the graph |
| `--probe` | Probe devices without the GUI; exits with code 3 if no battery is found |
| `--ui-smoke-test` | Open and close application windows; exits with code 2 on failure |

## Building and releases

Build and run tests on Windows with .NET SDK 10:

```powershell
dotnet test g-eBAR.sln -c Release --disable-build-servers -m:1
dotnet publish GeBar.App/GeBar.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Pushes and pull requests run tests, build both applications and check their windows. Build artifacts are available in GitHub Actions. A `vX.Y.Z` tag matching `Directory.Build.props` publishes a release. Previous releases and version tags are retained. Version history comes from GitHub Releases.

## Credits

- Developer: [lucz](https://github.com/byLucz)
- Testing: [Lslyrych](https://github.com/Lslyrych)
