# Surface Charging Tray

A small Windows system-tray utility that switches the Microsoft Surface app's
charging mode (Adaptive / Limit to 80% / Charge to 100% temporarily) without
you having to open the Surface app yourself.

> **Note** — this drives the modern Surface app's three-mode charging UI
> (Adaptive / Limit to 80% / Charge to 100% with 1-day or 1-week duration).
> Most Surface devices from Pro 8 / Laptop 5 onward have it. If your Surface
> app's *Battery & charging* page does not show those three radio buttons,
> this tool cannot drive them.

## Download

Grab the latest version from the [Releases](../../releases) tab. Three
packages are available:

| Package | When to use it | Install needed |
|---|---|---|
| `SurfaceChargingTray-x64.zip` | Intel-based Surfaces (most common). Also runs on Snapdragon Surfaces via Windows on ARM emulation. | None |
| `SurfaceChargingTray-arm64.zip` | Snapdragon Surfaces, native build (Pro 12, Pro X, Pro 11/Laptop 7 Snapdragon variants) | None |
| `SurfaceChargingTray-ahk.zip` | Universal — works on every Surface | [AutoHotkey v2](https://www.autohotkey.com/) (free, ~5 MB) |

To find your CPU: **Settings → System → About → System type**.

## What it does

- Plug icon in the system tray, follows your light/dark theme.
- Right-click for the menu, hover for the current mode.
- Pick a mode from the menu: Adaptive, Limit to 80%, Charge to 100%
  (1 day) or (1 week). A check mark shows the active mode.
- *Refresh status* — re-reads the actual state from the Surface app.
- *Open Surface app* — direct shortcut.
- *Settings...* — configure global keyboard hotkeys for any of the modes.
  Off by default. Suggested combos: Ctrl+Shift+1/2/3/4 for the four modes,
  Ctrl+Shift+B for "cycle through modes". Avoid Alt+Shift (input-language
  switcher) and plain Win+digit (taskbar slots).
- *Run at Windows login* — toggle: places a shortcut in your Startup folder.
- *Show last error* — opens a dialog with the most recent failure if a
  toggle didn't work.

## How it works

The Surface app is the only thing on Windows that exposes the three-mode
charging UI, and Microsoft offers no documented API to change it from
outside. So this tool briefly opens the Surface app **off-screen**, drives
the right radio button on its *Battery & charging* page through Windows
[UI Automation](https://learn.microsoft.com/en-us/windows/win32/winauto/entry-uiauto-win32),
then closes the app. The whole cycle takes a few seconds.

If the Surface app is already open when you click a tray menu item, the
tool closes and reopens it fresh — that way the *Battery & charging* card
is always reachable, even if you'd previously navigated to a different
page in the app.

The Surface app's package family name varies between Surface generations
(`Microsoft.SurfaceHub_8wekyb3d8bbwe`,
`MicrosoftCorporationII.MicrosoftSurface_8wekyb3d8bbwe`, etc.). Both
packages auto-detect this on first run via `Get-StartApps` (AHK) or the
WinRT `PackageManager` (EXE), so a fresh install on a different Surface
model just works.

## Repo layout

```
SurfaceChargingTray/
├── README.md          this file
├── LICENSE            MIT
├── build.ps1          rebuilds the EXE for both architectures
├── ahk/               AutoHotkey v2 source (run as-is, no compile)
│   ├── surface-tray.ahk
│   ├── surface-set-mode-hidden.ps1
│   ├── surface-get-status.ps1
│   └── *.ico
└── source/            C# source for the standalone EXE
    ├── SurfaceChargingTray.csproj
    └── *.cs
```

## Build from source

The AHK package is plain text — clone the repo and run `ahk/surface-tray.ahk`
directly with [AutoHotkey v2](https://www.autohotkey.com/).

The EXE needs the [.NET 8 SDK](https://dotnet.microsoft.com/download):

```powershell
git clone https://github.com/keyokku/SurfaceChargingTray
cd SurfaceChargingTray
powershell -File build.ps1            # both arm64 + x64
powershell -File build.ps1 -Arch x64  # just one
```

Output lands in `dist/<arch>/SurfaceChargingTray.exe` — single-file
self-contained (~70–75 MB), recipients don't need .NET installed.

## Compatibility

Tested on:
- Surface Pro 12 (Snapdragon) — native arm64 build and x64 build under Prism

Should work on any Surface whose Surface app exposes the three-mode
charging UI — verified package families: `Microsoft.SurfaceHub`,
`MicrosoftCorporationII.MicrosoftSurface`. Other Surface package family
names will be auto-detected at first launch via the Start menu listing.

## Contributing

Issues and PRs welcome. If you have a Surface model where this doesn't
work out of the box, an issue with your model + the contents of the
`surface-error.log` file is the most useful thing.

## License

MIT — see [LICENSE](LICENSE).

## Author

Made by [Keyokku](https://x.com/keyokku) ([u/keyokku](https://reddit.com/u/keyokku)).
If this is useful to you, a small tip is appreciated:
[ko-fi.com/keyokku](https://ko-fi.com/keyokku).
