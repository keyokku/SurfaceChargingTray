# Surface Charging Tray

https://github.com/user-attachments/assets/5aef04e3-2bf7-4bb8-b287-0b3265136ce2

<p align="center">
  <img width="520" alt="Surface Charging Tray screenshot" src="https://github.com/user-attachments/assets/bca4bf0c-df3b-48cf-90fc-46c85c68c4a0" />
</p>

A small Windows system-tray utility that switches the Microsoft Surface app's
**charging mode** (Adaptive / Limit to 80% / Charge to 100% temporarily) — and
the Windows 11 **Power mode** (Best efficiency / Balanced / Best performance) —
without you having to open Settings or the Surface app yourself.

> **Note** — the charging-mode side drives the modern Surface app's three-mode
> *Battery & charging* UI. Most Surface devices from Pro 8 / Laptop 5 onward
> have it. If your Surface app's *Battery & charging* page does not show those
> three radio buttons, this tool cannot drive them.

## Versions

All releases are kept available so you can pin to whichever you prefer.

| Version | Released | Highlights |
|---|---|---|
| **v1.2.0** *(coming soon)* | — | Charging Mode Scheduler — set a daily time and target charging mode (e.g. switch to *Charge to 100%* at 7am so the device is ready for the day). |
| **[v1.1.1](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.1.1)** *(latest)* | 2026-05-10 | Multi-language UIA Name lookup so non-English Surface app installs find the Battery & charging card. `.exe` build also adds first-launch auto-discovery + AutomationId caching, self-healing on schema changes, and a coalescing queue for rapid mode switches. AHK package gets the multi-language fix only. |
| **[v1.1.0](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.1.0)** | 2026-05-10 | Windows Power mode submenu (3 modes) + 3 Power-mode hotkey slots, persistent rotating logs, memory-leak fixes for long-running trays. AHK package gets the same Power-mode features. |
| **[v1.0.0](https://github.com/keyokku/SurfaceChargingTray/releases/tag/v1.0.0)** | 2026-05-09 | Initial release. Charging-mode tray icon, light/dark theme, configurable hotkeys for the four modes + cycle, auto-start at Windows login, three packages (arm64 / x64 / AHK). |

Full per-release notes: [CHANGELOG.md](CHANGELOG.md).

## Download

Get the latest from the [Releases](../../releases) tab. Three packages each release:

| Package | Use it on | Install needed |
|---|---|---|
| `SurfaceChargingTray-x64.zip` | Intel-based Surfaces (most common). Also runs on Snapdragon Surfaces via Windows on ARM emulation. | None |
| `SurfaceChargingTray-arm64.zip` | Snapdragon Surfaces, native build (Pro 12, Pro X, Pro 11/Laptop 7 Snapdragon variants) | None |
| `SurfaceChargingTray-ahk.zip` | Universal — any Surface | [AutoHotkey v2](https://www.autohotkey.com/) (free, ~5 MB) |

To find your CPU: **Settings → System → About → System type**.

To run: unzip anywhere, double-click `SurfaceChargingTray.exe` (or `surface-tray.ahk`). The tray icon appears immediately. No install, no admin, fully portable.

> **AHK package being phased out.** Native arm64 + x64 `.exe` builds now cover every supported Surface without needing AutoHotkey installed and are ahead on features (auto-discovery, ID caching, coalescing queue). The AHK package will continue to receive critical fixes for a while, but new features land in the `.exe` only. If you have a working setup, no rush — but new users should prefer the matching architecture's `.exe`.

## Compatibility

| Requirement | Charging modes | Power mode (v1.1.0+) |
|---|---|---|
| Operating system | Windows 10 build 19041 (20H1, May 2020) or newer | Windows 10 1809 (October 2018) or newer; Windows 11 recommended |
| Architecture | x64 + ARM64 (native builds for both) | x64 + ARM64 (native builds for both) |
| Surface device | Any with the modern *Battery & charging* UI (Pro 8 / Laptop 5 onward) | Any Windows device — does not require a Surface |
| Admin rights | None | None |

**Tested on:** Surface Pro 12 (Snapdragon) — native ARM64 build and x64 build under Prism.

Should work on any Surface whose Surface app exposes the three-mode charging UI. Verified Surface app package families: `Microsoft.SurfaceHub`, `MicrosoftCorporationII.MicrosoftSurface`. Other package family names auto-detect on first launch via the Start menu listing.

## Features

- **Charging mode tray menu** — Adaptive / Limit to 80% / Charge to 100% (1 day / 1 week). Check mark shows the active mode. Plug icon follows your light/dark theme.
- **Windows Power mode submenu** *(v1.1.0+)* — Best power efficiency / Balanced / Best performance. Sub-millisecond, no admin. On Surface devices that expose separate "Plugged in" / "On battery" Power mode dropdowns, both sides are set together so your choice persists across plug-in/unplug. For per-state granularity, use Windows Settings.
- **Configurable global hotkeys** for every mode (charging or Power). Off by default. Suggested combos: `Ctrl+Shift+1/2/3/4` for charging modes, `Ctrl+Shift+B` for "cycle through charging modes", `Ctrl+Shift+5/6/7` for Power modes. Avoid `Alt+Shift` (input-language switcher) and `Win+digit` (taskbar slots).
- **Refresh status** — re-reads the current mode from the Surface app, useful if you toggled it manually.
- **Open Surface app** — direct shortcut.
- **Run at Windows login** — toggle: places a shortcut in your Startup folder (per-user, no admin).
- **Show last error** — pops up the most recent failure if a toggle didn't work.

## Logs

Two log files live next to the .exe (so the package stays portable):

- `surface-error.log` — recent operational errors plus a `[INFO] Started v1.1.0.0` heartbeat on each launch. Capped at 500 lines, ISO-8601 timestamped.
- `crash.log` — captures unhandled .NET exceptions with full stack traces. Same rotating policy.

Both are safe to delete at any time. If you click something and nothing happens AND no log entry appears, the app died before reaching the click handler — open Windows Event Viewer → Applications and look for an `Application Error` entry naming `SurfaceChargingTray.exe` for the OS-level crash code.

## How it works

**Charging modes:** the Surface app is the only thing on Windows that exposes the three-mode charging UI, and Microsoft offers no documented API to change it from outside. So this tool briefly opens the Surface app **off-screen**, drives the right radio button on its *Battery & charging* page through Windows [UI Automation](https://learn.microsoft.com/en-us/windows/win32/winauto/entry-uiauto-win32), then closes the app. The whole cycle takes a few seconds. If the Surface app is already open when you click a tray menu item, the tool closes and reopens it fresh — that way the *Battery & charging* card is always reachable, even if you'd previously navigated to a different page in the app.

**Power modes:** uses `powrprof.dll` directly via P/Invoke (`PowerGetEffectiveOverlayScheme` to read, `PowerSetUserConfiguredACPowerMode` + `PowerSetUserConfiguredDCPowerMode` to write). No process launches, no UI, no admin. The reads run on a 5-second timer so the tray check marks stay in sync if you change Power mode from Windows Settings or if Windows auto-switches on AC/DC transitions.

> **Tip:** give a charging-mode switch a few seconds — and after you update hotkey settings, watch your taskbar even if you don't see the Surface app window come up. The Surface app activates briefly off-screen and may flash a taskbar entry while it's being driven.

The Surface app's package family name varies between Surface generations (`Microsoft.SurfaceHub_8wekyb3d8bbwe`, `MicrosoftCorporationII.MicrosoftSurface_8wekyb3d8bbwe`, etc.). Both packages auto-detect this on first run via `Get-StartApps` (AHK) or the WinRT `PackageManager` (EXE), so a fresh install on a different Surface model just works.

## Build from source

The AHK package is plain text — clone the repo and run `ahk/surface-tray.ahk` directly with [AutoHotkey v2](https://www.autohotkey.com/).

The EXE needs the [.NET 8 SDK](https://dotnet.microsoft.com/download):

```powershell
git clone https://github.com/keyokku/SurfaceChargingTray
cd SurfaceChargingTray
powershell -File build.ps1            # both arm64 + x64
powershell -File build.ps1 -Arch x64  # just one
```

Output lands in `dist/<arch>/SurfaceChargingTray.exe` — single-file self-contained (~70–75 MB compressed), recipients don't need .NET installed. A handful of WPF native sidecar DLLs ship in the same folder; the package stays fully portable (copy the folder anywhere).

## Repo layout

```
SurfaceChargingTray/
├── README.md         this file
├── CHANGELOG.md      per-release notes
├── LICENSE           MIT
├── build.ps1         rebuilds the EXE for both architectures
├── ahk/              AutoHotkey v2 source (run as-is, no compile)
│   ├── surface-tray.ahk
│   ├── surface-set-mode-hidden.ps1
│   ├── surface-get-status.ps1
│   └── *.ico
├── source/           C# source for the standalone EXE
│   ├── SurfaceChargingTray.csproj
│   └── *.cs
└── v2-archive/       abandoned "fast path" research; preserved for future
                      reference (see RESEARCH-NOTES.md inside)
```

## Contributing

Issues and PRs welcome. If you have a Surface model where this doesn't work out of the box, an issue with your model + the contents of `surface-error.log` is the most useful thing to share.

## License

MIT — see [LICENSE](LICENSE).

## Author

Made by [@Keyokku](https://x.com/keyokku) / [u/keyokku](https://reddit.com/u/keyokku).
If this is useful to you, a small tip is appreciated, but no obligation: [ko-fi.com/keyokku](https://ko-fi.com/keyokku).
