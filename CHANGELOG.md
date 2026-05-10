# Changelog

All releases are tagged in git and published as zip bundles on the
[GitHub Releases](https://github.com/keyokku/SurfaceChargingTray/releases) page.

---

## v1.1.0 — 2026-05-10

New features and resilience improvements. Fully backward-compatible with v1.0.0
settings; no migration steps required.

### New features

- **Windows Power mode submenu** in the tray. Switch between
  *Best power efficiency*, *Balanced*, and *Best performance* without opening
  Settings. Sub-millisecond, no admin needed. On Surface devices that expose
  separate "Plugged in" / "On battery" Power mode dropdowns, both sides are
  set together so your choice persists across plug-in/unplug.
- **Three additional configurable hotkey slots** for the new Power modes
  (defaults: `Ctrl+Shift+5/6/7`, off by default — enable in Settings).
- **Persistent rotating logs** next to the .exe:
  `surface-error.log` for operational errors plus a `[INFO] Started v1.1.0.0`
  heartbeat on each launch, and `crash.log` for unhandled .NET exceptions.
  Both capped at 500 lines, ISO-8601 timestamps, never grow unboundedly.

### Reliability fixes

- **ARM64 calling-convention crash fix.** Several `powrprof.dll` functions
  ship with a documented "GUID by value" signature, but on ARM64 Windows
  P/Invoke marshals 16-byte structs differently than x64 — passing by value
  triggered an `AccessViolationException` (0xC0000005) inside the function.
  All Power-mode P/Invoke now uses `ref Guid` (pointer), which works on both
  architectures.
- **Memory leak fixes.** Cached the tray icons / error bitmap so the 5-second
  theme tick no longer leaks `HICON` / `HBITMAP` handles over long uptimes.
  Periodic working-set trim keeps Task Manager's "Memory" column slim.
- **Hotkey activation race fix.** `AllowSetForegroundWindow` is now called
  inside the `WM_HOTKEY` window so Surface app activation succeeds reliably
  on background-thread launches.

### AHK package

- Same Power Mode features ported to the AutoHotkey package:
  three menu items, three hotkey slots, direct `powrprof.dll` calls (no
  PowerShell hop). Uses the same `ref Guid` pattern via `Buffer` for
  cross-architecture safety.

### Compatibility

- Targets Windows 10 build 19041 (20H1, May 2020) or newer. Power mode
  features require Win10 1809+ (October 2018) for the
  `PowerSetUserConfiguredAC/DCPowerMode` APIs; on older builds the menu
  is still shown but disabled.
- Native ARM64 and x64 .exe builds. AHK package works on either.
- Tested on Surface Pro 12 (Snapdragon ARM64).

### Notes for contributors

- `v2-archive/` records an attempted "fast path" via the Surface broker's
  gRPC interface (bypassing the Surface app UI entirely). Conclusion: the
  broker enforces Authenticode signature verification on calling processes
  via `WinVerifyTrust`, so unsigned third-party callers are silently
  rejected. Source code, .proto files, and a full research log are
  preserved for future reference.
- `FUTURE-IDEAS.md` sketches a Charging Mode Scheduler design (target:
  v1.2.0) using Windows Task Scheduler with `WakeToRun` so charge-mode
  switches can fire while the device sleeps.

---

## v1.0.0 — 2026-05-09

Initial public release.

- Tray icon switches the Surface app's charging mode (Adaptive / Limit to 80% /
  Charge to 100% with 1-day or 1-week duration) by driving the Surface app's
  *Battery & charging* radio buttons via UI Automation. The Surface app opens
  briefly off-screen and closes again — no UI flash beyond a tiny window.
- Light/dark theme follows Windows automatically.
- Configurable global hotkeys for all four modes plus a "cycle modes" action.
- Auto-start at Windows login (per-user shortcut, no admin).
- Three packages: `arm64.zip` (native Snapdragon), `x64.zip` (Intel), and an
  AutoHotkey v2 source package for either architecture.
