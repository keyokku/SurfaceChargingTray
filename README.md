# Surface Charging Tray

Older version visuals (will update later):

https://github.com/user-attachments/assets/5aef04e3-2bf7-4bb8-b287-0b3265136ce2

<p align="center">
  <img width="520" alt="Surface Charging Tray screenshot" src="https://github.com/user-attachments/assets/bca4bf0c-df3b-48cf-90fc-46c85c68c4a0" />
</p>

A small Windows system-tray utility that switches the Microsoft Surface app's
**charging mode** (Adaptive / Limit to 80% / Charge to 100% temporarily) — and
the Windows 11 **Power mode** (Best efficiency / Balanced / Best performance) —
without you having to open Settings or the Surface app yourself. Now with a
built-in scheduler so you can have charging mode flip overnight on its own.

> **Note** — the charging-mode side drives the modern Surface app's three-mode
> *Battery & charging* UI. Most Surface devices from Pro 8 / Laptop 5 onward
> have it. If your Surface app's *Battery & charging* page does not show those
> three radio buttons, this tool cannot drive them.
>
> **Known Surface-app issue (not us):** on some installs the Surface app
> intermittently fails to render the *Battery & charging* card even when you
> open the app manually — it appears blank, missing, or stuck loading. This
> is a long-standing Surface-app-side bug; reports go back to 2023 and the
> usual fix is to **uninstall and reinstall the Surface app** from the
> Microsoft Store. If our tool reports "card not found" and a manual launch
> of the Surface app also doesn't show the card, it's almost certainly this
> bug rather than something this tool is doing.

## Features

- Tray icon and live status
- Tray menu or configurable keyboard shortcuts
- Quick-change Surface charging modes (Adaptive, 80%, 100%)
- **Schedule a charge-mode change during "sleep"** (e.g. flip 80% → 100% in the morning)
- Quick-change Windows Power mode (Performance, Balanced, Efficiency)
- Run at Windows login
- Auto-detects your language localization, Surface app UI, and current charging mode
- Hides the Surface app during operation
- Persistent error log next to the .exe
- *Surface charging modes cannot be changed while the device is asleep / locked — see the scheduler below for the workaround*

## Versions

All releases are kept available so you can pin to whichever you prefer.

| Version | Released | Highlights |
|---|---|---|
| **[v1.2.0](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.2.0)** *(latest)* | 2026-05-11 | Charging-mode scheduler. Press a hotkey before bed; the device stays active behind a black overlay until your set time, flips charging mode, then lets the device sleep normally. See [Charging-mode scheduler](#charging-mode-scheduler-how-it-works) below. **No AHK package this release — `.exe` builds only.** |
| **[v1.1.1](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.1.1)** | 2026-05-10 | Multi-language UIA Name lookup so non-English Surface app installs find the Battery & charging card. `.exe` build also adds first-launch auto-discovery + AutomationId caching, self-healing on schema changes, and a coalescing queue for rapid mode switches. AHK package gets the multi-language fix only. |
| **[v1.1.0](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.1.0)** | 2026-05-10 | Windows Power mode submenu (3 modes) + 3 Power-mode hotkey slots, persistent rotating logs, memory-leak fixes for long-running trays. AHK package gets the same Power-mode features. |
| **[v1.0.0](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.0.0)** | 2026-05-09 | Initial release. Charging-mode tray icon, light/dark theme, configurable hotkeys for the four modes + cycle, auto-start at Windows login, three packages (arm64 / x64 / AHK). |

Full per-release notes: [CHANGELOG.md](CHANGELOG.md).

## Download

Get the latest from the [Releases](../../releases) tab. Two packages this release:

| Package | Use it on |
|---|---|
| `SurfaceChargingTray-v1.2.0-arm64.zip` | Snapdragon Surfaces, native build (Pro 12, Pro X, Pro 11 / Laptop 7 Snapdragon variants) |
| `SurfaceChargingTray-v1.2.0-x64.zip` | Intel-based Surfaces (most common). Also runs on Snapdragon Surfaces via Windows on ARM emulation. |

To find your CPU: **Settings → System → About → System type**.

**Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime)** (~55 MB, one-time install). If you don't have it yet, Windows pops up a dialog with a direct download link when you first launch the app. Many other modern Windows apps (PowerToys, Files, Windows Terminal) already use .NET 8, so you may already have it.

To run: unzip anywhere, double-click `SurfaceChargingTray.exe`. The tray icon appears immediately. No install, no admin, fully portable.

> **No AHK package this release.** The AutoHotkey version is being phased out
> as announced in v1.1.1. The simulated-sleep scheduler depends on Windows
> APIs (overlay rendering, brightness control, execution-state locking, WMI)
> that aren't practical to implement in AHK. Existing AHK users can continue
> running v1.1.1; v1.2.0+ features are `.exe` only.

## Compatibility

| Requirement | Charging modes | Power mode | Scheduler |
|---|---|---|---|
| Operating system | Windows 10 build 19041 (May 2020) or newer | Windows 10 1809 (Oct 2018) or newer; Win 11 recommended | Windows 10 build 19041 or newer |
| .NET runtime | .NET 8 Desktop Runtime (free, ~55 MB) | same | same |
| Architecture | x64 + ARM64 (native builds for both) | same | same |
| Surface device | Any with the modern *Battery & charging* UI (Pro 8 / Laptop 5 onward) | Any Windows device — does not require a Surface | Same as charging |
| Admin rights | None | None | None |
| Plugged in | Recommended | — | **Required** (refuses on battery) |

**Tested on:** Surface Pro 12 (Snapdragon) — native ARM64 build, overnight scheduler run.

## How charging / Power modes work under the hood

**Charging modes:** the Surface app is the only thing on Windows that exposes the three-mode charging UI, and Microsoft offers no documented API to change it from outside. So this tool briefly opens the Surface app **off-screen**, drives the right radio button on its *Battery & charging* page through Windows [UI Automation](https://learn.microsoft.com/en-us/windows/win32/winauto/entry-uiauto-win32), then closes the app. The whole cycle takes a few seconds.

**Power modes:** uses `powrprof.dll` directly via P/Invoke. No process launches, no UI, no admin. The reads run on a 5-second timer so the tray check marks stay in sync if you change Power mode from Windows Settings or if Windows auto-switches on AC/DC transitions.

The Surface app's package family name varies between Surface generations (`Microsoft.SurfaceHub_8wekyb3d8bbwe`, `MicrosoftCorporationII.MicrosoftSurface_8wekyb3d8bbwe`, etc.). Auto-detected on first run via the WinRT `PackageManager`, so a fresh install on a different Surface model just works.

## Charging-mode scheduler — how it works

### The problem

Building on the mechanism above: the Surface app **only renders its UI while the device is actually awake and active**. When the device is asleep, locked, or has its screen off, Windows defers UWP rendering — the UI tree is dormant and UI Automation finds nothing to click. So a naive scheduled task that fires at 5 AM with the device asleep simply does nothing.

### The workaround: "simulated sleep"

Toggling the schedule hotkey enters a **simulated sleep** state instead of letting the device actually sleep:

- A fullscreen black overlay covers every monitor (looks the same as sleep)
- Screen brightness drops to 0
- Windows Power mode switches to *Best efficiency*
- `SetThreadExecutionState` flag is held — Windows treats the device as "active" and skips its normal Sleep / Screen-off timers, but **without permanently editing your power settings** (the override auto-reverts when simulated sleep exits)

To Windows, the device is still awake and rendering, so the Surface app stays drivable. To you, the device looks asleep.

At your scheduled time, the tool flips the charging mode in the background while the overlay stays up. After that, you have two options:

- **Stay in simulated sleep** — the overlay stays black until you click or press a key
- **Exit simulated sleep + allow real sleep** — the overlay tears down, brightness restores, Windows' actual Sleep / Screen-off timers kick in immediately (since there's been no input for hours). The device falls into real sleep on its own

Either way, your original brightness and Power-mode setting are restored when simulated sleep ends.

### How to use it

1. Right-click the tray icon → **Settings...** → **Schedule** tab
2. **Charging mode** — pick what you want to switch to (e.g. *Charge to 100% (1 day)*)
3. **Scheduled time** — set the time it should fire (e.g. `05:30` for early morning)
4. **After the fire** — pick *Stay in simulated sleep* or *Exit simulated sleep and allow real sleep timeouts* (recommended for overnight charging)
5. **Toggle hotkey** — enable and set a combo (default: `Ctrl+Shift+T`)
6. **Save**
7. **Before bed** — plug in. Press the toggle hotkey. Screen goes black. Walk away
8. **In the morning** — the device is either in real sleep (if you picked the second option) or still in simulated sleep waiting for you. Either way, charging mode has been switched at your scheduled time. Press any key or click to wake / dismiss

### Caveats

- **Plugged in only.** Simulated sleep keeps the device active, which drains the battery. The tool refuses to enter on battery and shows a dialog warning if you press the hotkey while unplugged.
- **Don't close the lid / press the power button / Win+L during simulated sleep.** Those are hardware signals Windows treats as sleep / lock regardless of our flag — they take the device out of simulated-sleep state and the charging-mode flip will fail (Surface app dormant again).
- **The screen is technically still on** behind the overlay. Brightness is 0 but the panel draws power. This is the trade-off for keeping the Surface app drivable.
- **No user activity is generated.** The mouse cursor doesn't move; Windows just thinks "an app is requesting display attention." Software that pings on user idle (e.g. status indicators that say "away after 5 min") will behave as if the device is idle. That's fine.
- **Crash recovery.** If the tray crashes during simulated sleep, the brightness and Power-mode originals are saved to a small recovery file and restored automatically on the next launch.

## Logs

Two log files live next to the .exe (so the package stays portable):

- `surface-error.log` — operational errors plus a `[INFO] Started v1.2.0.0` heartbeat on each launch and scheduled-fire result lines. Capped at 500 lines, ISO-8601 timestamped.
- `crash.log` — captures unhandled .NET exceptions with full stack traces. Same rotating policy.

Both are safe to delete at any time. If you click something and nothing happens AND no log entry appears, the app died before reaching the click handler — open Windows Event Viewer → Applications and look for an `Application Error` entry naming `SurfaceChargingTray.exe` for the OS-level crash code.

## Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download):

```powershell
git clone https://github.com/keyokku/SurfaceChargingTray
cd SurfaceChargingTray
powershell -ExecutionPolicy Bypass -File build.ps1            # both arm64 + x64
powershell -ExecutionPolicy Bypass -File build.ps1 -Arch x64  # just one
```

Output lands in `dist/v1.2.0/<arch>/SurfaceChargingTray.exe` — single-file framework-dependent (~25 MB). Recipients need the .NET 8 Desktop Runtime installed (free, one-time).

## Repo layout

```
SurfaceChargingTray/
├── README.md          this file
├── CHANGELOG.md       per-release notes
├── LICENSE            MIT
├── build.ps1          rebuilds the EXE for both architectures
├── ahk/               AutoHotkey v2 source from older releases (no longer maintained)
├── source/            C# source for the standalone EXE
│   ├── SurfaceChargingTray.csproj
│   └── *.cs
└── v2-archive/        abandoned "fast path" research; preserved for future
                       reference (see RESEARCH-NOTES.md inside)
```

## Contributing

Issues and PRs welcome. If you have a Surface model where this doesn't work out of the box, an issue with your model + the contents of `surface-error.log` is the most useful thing to share.

## License

MIT — see [LICENSE](LICENSE).

## Author

Made by [@Keyokku](https://x.com/keyokku) / [u/keyokku](https://reddit.com/u/keyokku).
If this is useful to you, a small tip is appreciated, but no obligation: [ko-fi.com/keyokku](https://ko-fi.com/keyokku).
