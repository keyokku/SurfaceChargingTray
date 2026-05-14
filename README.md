# Surface Charging Tray

Older version visuals (will update later):

https://github.com/user-attachments/assets/5aef04e3-2bf7-4bb8-b287-0b3265136ce2

<p align="center">
  <img width="600" height="703" alt="image" src="https://github.com/user-attachments/assets/e5261918-5ffc-4e40-a5ea-ad12815427c4" />
</p>


A small Windows system-tray utility that switches the Microsoft Surface app's
**charging mode** (Adaptive / Limit to 80% / Charge to 100% temporarily) — and
the Windows 11 **Power mode** (Best efficiency / Balanced / Best performance) —
without you having to open Settings or the Surface app yourself. Now with a
built-in scheduler so you can have charging mode flip overnight on its own.

> **⚠️ Will this work on my Surface?**
>
> This tool drives the Surface app's **three-mode** *Battery & charging* UI
> (Adaptive / Limit to 80% / Charge to 100%). To check: open the Surface app
> yourself and find the *Battery & charging* card. If you see those three
> radio buttons, you're good. If you see a single *Charge to 100%* toggle
> instead, your device's Surface app uses an older UI variant that this tool
> can't drive yet (planned for v1.3.0).
>
> **Likely working:** Surface Pro 8 onward · Surface Laptop 5 onward ·
> Surface Laptop Studio 2 onward.
>
> **Not yet supported:** Surface Laptop Studio 1 · Surface Pro 7
> and earlier (single-toggle UI).

> **Work in progress** — only tested on a few devices so far. Issues may exist
> and reports are appreciated; see *Flagging issues in Github* below.

> **Known Surface-app bug (not us):** on some installs the Surface app
> intermittently fails to render the *Battery & charging* card even when you
> open the app manually. This is a long-standing Surface-app-side issue; the
> usual fix is to **uninstall and reinstall the Surface app** from the
> Microsoft Store.

## Features

- Tray icon and live status
- Tray menu or configurable keyboard shortcuts
- Quick-change Surface charging modes (Adaptive, 80%, 100%)
- **Schedule a charge-mode change during "sleep"** (e.g. flip 80% → 100% in the morning)
- Quick-change Windows Power mode (Performance, Balanced, Efficiency)
- Run at Windows login
- Auto-detects your language localization, Surface app UI, and current charging mode
- Hides the Surface app during operation

## Versions

All releases are kept available so you can pin to whichever you prefer.

| Version | Released | Highlights |
|---|---|---|
| **[v1.2.2](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.2.2)** *(latest)* | 2026-05-12 | Charging-mode scheduler. Press a hotkey before bed; the device enters simulated sleep to change charging mode, then lets the device sleep normally. See [Charging-mode scheduler](#charging-mode-scheduler) below. **Adds a separate diagnostic tool download for users whose Surface model isn't detected, plus better error dialog that links. No AHK package — `.exe` builds only.** |
| **[v1.1.1](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.1.1)** | 2026-05-10 | Multi-language UIA Name lookup so non-English Surface app installs find the Battery & charging card. `.exe` build also adds first-launch auto-discovery + AutomationId caching, self-healing on schema changes, and a coalescing queue for rapid mode switches. AHK package gets the multi-language fix only. |
| **[v1.1.0](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.1.0)** | 2026-05-10 | Windows Power mode submenu (3 modes) + 3 Power-mode hotkey slots, persistent rotating logs, memory-leak fixes for long-running trays. AHK package gets the same Power-mode features. |
| **[v1.0.0](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.0.0)** | 2026-05-09 | Initial release. Charging-mode tray icon, light/dark theme, configurable hotkeys for the four modes + cycle, auto-start at Windows login, three packages (arm64 / x64 / AHK). |

Full per-release notes: [CHANGELOG.md](CHANGELOG.md).

## Download

Latest release on the [Releases](../../releases) tab. Two packages:

| Package | For |
|---|---|
| `SurfaceChargingTray-v1.2.2-arm64.zip` | Snapdragon Surfaces (Pro X / 11 / 12, Laptop 7 Snapdragon) |
| `SurfaceChargingTray-v1.2.2-x64.zip` | Intel-based Surfaces (most common). Also runs on Snapdragon via Prism emulation. |

Find your CPU at **Settings → System → About → System type**. Unzip, double-click `SurfaceChargingTray.exe`. No install, no admin.

Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) , a Windows standard framework (i.e. PowerToys, Terminal, Files). Windows offers a direct install link on first launch if missing.

## Compatibility

- **Windows 10 build 19041 (May 2020) or newer**; Windows 11 recommended
- **.NET 8 Desktop Runtime** (~55 MB, free, one-time install)
- **x64 or ARM64** — native builds for both
- **No admin rights** required
- Scheduler **requires the device to be plugged in**

**Tested on:** Surface Pro 12 (Snapdragon ARM64).

## Under the hood

**Charging modes** — Microsoft offers no documented API to change them, so the tool briefly opens the Surface app **off-screen**, drives the right radio button on its *Battery & charging* page via Windows [UI Automation](https://learn.microsoft.com/en-us/windows/win32/winauto/entry-uiauto-win32), then closes the app. The whole cycle takes a few seconds.

**Power modes** — calls `powrprof.dll` via P/Invoke. No process launches, no UI, no admin. A 5-second timer keeps the tray check marks in sync with external Power-mode changes.

The Surface app's package family name varies between Surface generations; auto-detected on first run via the WinRT `PackageManager`, so a fresh install on a different Surface model just works.

## Charging-mode scheduler

Surface charging modes can only be changed through the Surface app's UI, and Windows doesn't render that UI when the device is truly asleep or has its screen off — so a normal scheduled task at 5 AM doesn't activate. The scheduler works around this with "simulated sleep": only use if needed.

### How to use it

1. Right-click the tray icon → **Settings...** → **Schedule** tab
2. **Charging mode** — pick what you want to switch to (e.g. *Charge to 100% (1 day)*)
3. **Scheduled time** — set the time it should fire (e.g. `05:30`)
4. **After the fire** — pick *Stay in simulated sleep* or *Exit simulated sleep and allow real sleep timeouts* (recommended for overnight charging)
5. **Toggle hotkey** — enable and set a combo (default: `Ctrl+Shift+T`)
6. **Save**
7. **Before bed** — plug in, press the toggle hotkey. The tool:
   - Covers every monitor with a black overlay (looks like sleep)
   - Drops brightness to 0
   - Switches Windows Power mode to *Best efficiency*
   - Holds an exec-state flag so Windows treats the device as awake — overrides your Sleep / Screen-off timers without permanently editing them
8. **At the scheduled time** — the charging mode flips in the background while the overlay stays up. Your original brightness and Power mode are restored when simulated sleep ends
9. **In the morning** — the device is either in real sleep (auto-exit option) or still in simulated sleep waiting for you. Press any key or click to wake/dismiss

### Caveats

- **Plugged in only.** Simulated sleep keeps the device active, which would drain the battery. Refuses to enter on battery.
- **Don't close the lid / press the power button / Win+L during simulated sleep.** Hardware signals override our flag — they put the device into real sleep or locked state, and the scheduled charging-mode flip will fail (Surface app dormant again).
- **The screen is technically on** behind the overlay. Brightness is 0 but the panel still draws power.
- **No user activity is generated.** Software that pings on user idle (e.g. "away after 5 min") will behave as if the device is idle. That's fine.
- **Crash recovery** — if the tray crashes during simulated sleep, the brightness and Power-mode originals are restored automatically on the next launch.

## Troubleshooting and flagging issues in Github

If something goes wrong, take it in this order:

### 1. Check the log files

`surface-error.log` and `crash.log` live next to the .exe — auto-rotating at 500 lines so they stay small. **Please attach them when filing a report** — they contain the diagnostic info needed to debug.

### 2. Run the diagnostic tool

If you see "card not found" or "no charging-mode radios appear selected", your Surface app's UI is structured differently than what this tool expects. The **diagnostic tool** captures your Surface app's full UI tree + a screenshot + system info so the developer can support your device in a future update.

- Download **[`SurfaceChargingTrayDiagnostic.zip`](https://github.com/keyokku/SurfaceChargingTray/releases/latest)** from the latest Release. It's attached as a separate asset alongside the main app's zips.
- Unzip, then run the `.exe` matching your CPU (x64 or arm64).
- Click **Run Test** in the dialog. The tool launches the Surface app if needed, scans the UI, and writes a `.txt` + `.png` next to the diagnostic exe.
- Post both files as a comment on the **[diagnostic-results thread](https://github.com/keyokku/SurfaceChargingTray/issues/2)**. One sentence about what you were doing helps.

### 3. Or, file a separate issue

For other problems (crashes, scheduler issues, UI bugs in the tray), [open a new GitHub issue](https://github.com/keyokku/SurfaceChargingTray/issues/new). Please include:

- Windows version
- Surface device model
- Surface app version
- Charging tray app version (latest is v1.2.2)
- Screenshot of the error dialog, plus a screenshot of your Surface app manually opened (charging panel open if possible)
- `settings.ini`, `surface-error.log`, `crash.log` from the program folder
- Steps to reproduce / what you were doing when you hit the error
- For scheduler issues: Windows sleep/screen-off settings + what you did during simulated sleep
- Any extra details or video capture if possible

This project is a work in progress and tested on only a couple of devices, so reports are appreciated and I'll patch as I can.

## Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download):

```powershell
git clone https://github.com/keyokku/SurfaceChargingTray
cd SurfaceChargingTray
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output: `dist/v1.2.2/<arch>/SurfaceChargingTray.exe` (~25 MB framework-dependent).

## Contributing

Issues and PRs welcome. If you have a Surface model where this doesn't work out of the box, an issue with your model + the contents of `surface-error.log` is the most useful thing to share.

## License

MIT — see [LICENSE](LICENSE).

## Author

Made by [@Keyokku](https://x.com/keyokku) / [u/keyokku](https://reddit.com/u/keyokku).
If this is useful to you, a small tip is appreciated, but no obligation: [ko-fi.com/keyokku](https://ko-fi.com/keyokku).
