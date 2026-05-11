# Changelog

All releases are tagged in git and published as zip bundles on the
[GitHub Releases](https://github.com/keyokku/SurfaceChargingTray/releases) page.

---

## v1.2.0 — 2026-05-11

Charging-mode scheduler. Set a daily time and a target charging mode and the
tool flips it overnight on its own — no Windows Task Scheduler, no waking
the device, no UI flash.

### What's new

- **Charging-mode scheduler with "simulated sleep"** — set a mode + a time
  (e.g. *Charge to 100% (1 day)* at `05:30`), bind a hotkey, and press the
  hotkey before bed. The tool covers every monitor with a fullscreen black
  overlay, drops brightness to 0, sets Windows Power mode to *Best
  efficiency*, and holds `SetThreadExecutionState(SYSTEM | DISPLAY)` so
  Windows treats the device as awake without modifying the user's Sleep /
  Screen-off timeouts. At the scheduled time the Surface app's charging
  mode is changed in the background while the overlay stays up. See
  [Charging-mode scheduler](README.md#charging-mode-scheduler-how-it-works)
  in the README for the full why-and-how.
- **After-fire behavior options:**
  - *Stay in simulated sleep* — overlay stays until you click or press a key.
  - *Exit simulated sleep and allow real sleep timeouts* — overlay tears
    down, brightness restores, and Windows' actual Sleep / Screen-off
    timers fire immediately. The device falls into real sleep on its own
    after the charging mode has switched.
- **Settings dialog with Schedule tab.** Charging mode dropdown, duration
  dropdown (for 100%), hour/minute dropdowns (no free-text input — can't
  enter an invalid time), Clear link, after-fire radio buttons, dedicated
  schedule-toggle hotkey row. Inline validation: incomplete time selection
  greys out the Save button and shows a red status line.
- **Picking the hour auto-fills minute to `:00`** for one-click on-the-hour
  scheduling.
- **Tray menu** gets a *Schedule* item above the Power mode submenu showing
  the saved schedule (e.g. `Schedule: 05:30 — 100% 1d` or `Schedule: (not set)`).
  Click it to jump straight to the Schedule tab.
- **Plugged-in guard.** Refuses to enter simulated sleep on battery
  (would drain the battery while the device is held active). Shows a modal
  warning dialog rather than a balloon-tip toast, so Windows Focus Assist /
  Do Not Disturb cannot suppress the notification.
- **Crash recovery.** Brightness and Power-mode originals are persisted to
  a small JSON file before mutation; if the tray crashes mid-simulated-sleep,
  next launch silently restores them.
- **"Run at Windows login" moved into the Settings dialog** (Hotkeys tab).
  Previously a tray-menu toggle.

### Reliability and cleanup

- Removed dev / prototype tray menu items used during v1.2.0 development.
- Removed Windows Task Scheduler installer code — the simulated-sleep
  scheduler is in-process and replaces it.
- Trimmed verbose `[INFO]` logging so a clean overnight run only writes a
  handful of log lines (start, scheduled-fire armed, scheduled-fire result).

### No AHK package this release

The AHK package is not built for v1.2.0 and is no longer maintained — the
simulated-sleep mechanic relies on Windows APIs (overlay rendering, WMI
brightness control, `SetThreadExecutionState`, multi-monitor topmost form
handling) that aren't practical to implement in AutoHotkey. As noted in
v1.1.1, the `.exe` builds had already reached feature parity and beyond
for every supported architecture. Existing AHK users can continue running
v1.1.1 indefinitely; v1.2.0+ features are `.exe` only.

### Distribution

- Framework-dependent single-file builds (~25 MB each, down from the
  ~70 MB self-contained builds of v1.0–v1.1.x). Requires .NET 8 Desktop
  Runtime — Windows offers a direct download link if it's missing.

### Compatibility

Same OS / device requirements as v1.1.1. The scheduler requires the
device to be plugged in.

---

## v1.1.1 — 2026-05-10

Reliability and internationalization patch. Fully backward-compatible
with v1.1.0 settings.

### What's new

- **Multi-language UIA Name lookup** for the Surface app's Battery & charging
  card and the three charging-mode radios. The old build relied on the
  English label `"Battery & charging"`; non-English Surface app installs
  hit a "card not found" error. v1.1.1 ships with a bundled list of common
  localized labels (English variants + ~15 European/Asian locales) and
  tries each one in turn. The duration combo items ("1 day" / "1 week")
  are now identified by substring match across major languages.
- **Auto-discovery and ID caching (`.exe` build only).** On first launch,
  the tray runs a background scan that expands every collapsible element
  in the Surface app and captures each element's `AutomationId` and
  `Name` into `[uia-cache]` in `settings.ini`. Subsequent menu clicks
  hit the cache on the first poll instead of doing a 15-second name search.
- **Self-healing on schema changes (`.exe` build only).** If a future
  Surface app update changes the labels or structure, the layered lookup
  falls through to the discovery scan as a one-shot fallback (then re-caches),
  so the tray adapts without a code release.
- **Coalescing queue for rapid mode switches (`.exe` build only).** Clicking
  modes back-to-back used to silently drop the second click. The queue now
  holds the most recent request and runs it after the in-flight operation
  completes — latest-click-wins. Tray tooltip says "queued: …" while
  waiting. Same coalescing applies to refresh.

### Reliability fixes

- Removed `"Battery & charging"` from the transient-retry path. Discovery
  is now a deliberate one-shot fallback; no more "Surface app opens twice"
  on persistent failures.
- Layer 1 cache lookup requires AutomationId AND Name to both match
  (Microsoft assigns generic IDs like `"Expander"` to many cards;
  ID-alone could resolve to the wrong element).

### AHK package

The AHK package gets the same multi-language Name lookup in both PS scripts
(`surface-set-mode-hidden.ps1` and `surface-get-status.ps1`) so non-English
Surface app installs work there too. The other v1.1.1 features (auto-
discovery, ID caching, coalescing queue) are `.exe`-only by design — keeping
the AHK scripts simple and inspectable.

> **Heads up:** the AHK package is being slowly phased out in upcoming
> releases. The `.exe` build now reaches feature parity and beyond on every
> supported architecture (native arm64 + x64), without needing AutoHotkey
> installed. The AHK package will continue to receive critical fixes for
> a while but new features will land in the `.exe` only.

### Compatibility

Same as v1.1.0 — Windows 10 build 19041 or newer, native arm64 + x64,
AHK package universal.

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

- **Memory leak fixes.** Cached the tray icons / error bitmap so the 5-second
  theme tick no longer leaks `HICON` / `HBITMAP` handles over long uptimes.
  Periodic working-set trim keeps Task Manager's "Memory" column slim.
- **Hotkey activation race fix.** `AllowSetForegroundWindow` is now called
  inside the `WM_HOTKEY` window so Surface app activation succeeds reliably
  on background-thread launches.

### AHK package

- Same Power Mode features ported to the AutoHotkey package: three menu
  items, three hotkey slots, direct `powrprof.dll` calls (no PowerShell hop).

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
