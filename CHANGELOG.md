# Changelog

All releases are tagged in git and published as zip bundles on the
[GitHub Releases](https://github.com/keyokku/SurfaceChargingTray/releases) page.

---

## v1.3.0 — 2026-05-16

Auto-detects two distinct Surface app UI shapes and shapes the entire
feature surface around what the device actually supports. Fully backward-
compatible with v1.2.x — existing users upgrading on a three-radio
Surface (Pro 9/10/11/12, Laptop 5/6/7 etc.) see no UX change.

### Two-variant device support

Surface devices ship the Battery & charging card in one of two shapes:

- **Variant A** — the three-radio classic targeted in v1.0–v1.2:
  Adaptive / Charge to 80% / Charge to 100%, plus a contextual one-shot
  "Charge to 100%" override button when 80% is selected.
- **Variant B** — a single one-shot "Charge to 100%" override button.
  No radios, no mode concept; the device manages Smart Charging implicitly,
  and the user's only available action is "override to 100% for this
  charge cycle." Observed on older Surfaces and on certain Surface app
  builds where Smart Charging is paused/limited.

v1.3.0 classifies the device structurally on first interaction (no new
multilingual strings — only the card title remains a translated lookup;
the inside of the card is detected by structure: three radios = A, one
invokable button = B). Result is cached to `settings.ini` alongside the
detection logic's app version.

### Variant B path (new)

For variant B users, the entire app reshapes:

- **Tray menu**: shows only "Charge to 100%" + Schedule + Power mode +
  housekeeping. The four variant-A mode items are hidden.
- **Settings dialog**: variant-aware info banner at top of Hotkeys tab
  + new "Re-detect device" button. Hotkey rows show only the one-shot
  action; variant-A mode rows are hidden.
- **Hotkeys**: new `oneshot` slot with default Ctrl+Shift+1. Variant
  filter prevents collisions with variant-A defaults at registration time.
- **Scheduler**: same simulated-sleep engine variant A uses, but the
  scheduled action is a single "Trigger Charge to 100%" instead of a
  mode flip. Time picker, after-fire radios, and toggle hotkey all
  unchanged.
- **RefreshState**: skips the variant-A radio walk for variant B
  devices, so the misleading "no radio appears selected" error no
  longer fires.

### Diagnostic tool

- Diagnostic output now includes a `Detected variant: A | B | Unknown`
  line alongside the existing card detection report. Future bug reports
  surface variant classification at a glance.

### Compatibility

- Existing v1.2.x users on variant A devices: zero UX change. First
  refresh after upgrade detects + caches A; all menus, hotkeys, and
  scheduler behavior identical to v1.2.2.
- Existing settings.ini files are forward-compatible. New fields
  (`detected_variant`, `detected_at_app_version`, `oneshot_button_*`)
  appear on first refresh; v1.2.x cache entries are preserved unchanged.
- Same OS / device requirements as v1.2.2. .NET 8 Desktop Runtime required.

### Internal

- `FakeSleepMode.Enter` generalized from `(scheduledMode, scheduledDuration)`
  kwargs to a polymorphic `ScheduledAction` (with `SetModeAction` and
  `TriggerOneShotAction` subtypes). Variant-A kwargs path preserved as
  a back-compat shim.
- `SurfaceController.TriggerOneShot` mirrors `SetMode`'s plumbing
  (acquire / bind / lookup / cache-poison-retry / cleanup) but ends in
  `InvokePattern.Invoke()` on the cached variant-B button. Guards
  against clicking a disabled button with a clear error.
- Diagnostic tool gains its DetectVariant call via the existing
  compile-include of UiaCache; no namespace work needed.

---

## v1.2.2 — 2026-05-12

Diagnostic tool + better error-state guidance for users hitting "card not
found" or "no radios appear selected" on Surface app variants we don't
yet support. Fully backward-compatible with v1.2.x settings.

### New: diagnostic tool

- **`SurfaceChargingTrayDiagnostic.zip`** is now attached as a separate
  download alongside the main app's release zips. Contains a small
  standalone tool that launches the Surface app, captures its complete
  UIA tree + a window screenshot + system info, and writes the output
  next to the .exe. Users post the output on the diagnostic-results
  thread (issue #2) so I can support their Surface model in v1.3.0.
- The tool is versionless (intentionally — it's a generic capture
  utility); the same zip is attached to every future release.
- Bundles both arm64 and x64 exes in one zip; users pick the right
  one for their CPU.

### Main app

- **Custom error dialog** replaces the plain MessageBox when "Show last
  error" surfaces a detection-failure (e.g. "card not found", "radios
  appear selected"). New dialog explains what happened in one paragraph
  and offers two clickable buttons: *Download diagnostic tool* (deep-
  links to the latest Releases page) and *Open GitHub thread* (deep-
  links to the diagnostic-results thread). Other (non-detection) errors
  still get the simple display.
- **Added German locale variant** `"Akku und Laden"` to the
  Battery & charging name list — observed on Surface Laptop Studio 1's
  Surface app build.
- **Tree snapshot diagnostic deepened**: when the detection-failure
  errors fire, the UIA tree dump in `surface-error.log` now goes 5
  levels deep (was 3) and captures up to 120 elements (was 60).
  Surface app cards live at depth ~4-5 so the previous limit was
  cutting off where the diagnostic info became useful.

### Compatibility

Same OS / device requirements as v1.2.1. .NET 8 Desktop Runtime required.

---

## v1.2.1 — 2026-05-12

Detection-and-discovery hardening patch for users on Surface devices /
locales / Surface app builds that the v1.2.0 baseline didn't recognize.
Fully backward-compatible with v1.2.0 settings.

### Detection improvements

- **More lenient structural search for the Battery & charging card.**
  Previously the structural fallback required a Group with *exactly* 3+
  RadioButton descendants. New strategies in order: 3+ radios → 2+ radios
  → any element whose AutomationId contains "Battery" / "Charging" /
  "ChargeMode" → walk-up from any RadioButton whose Name matches a known
  charging mode. Catches Surface app builds where the wrapper element is
  a Pane instead of a Group, or where the radio count differs.
- **Multi-pattern selection detection.** When reading which mode is
  currently active, the tool now tries `SelectionItemPattern.IsSelected`
  (canonical) and then `TogglePattern.ToggleState == On`. Surface app
  builds that implement the mode controls as toggle-buttons (rather
  than radio-buttons) now work where previously the "None of the three
  radios appear selected" error fired.
- **Cache-poisoning recovery.** If a previously-cached card (from an
  earlier first-launch discovery) leads to "radio not found" or "no
  selection" downstream, the tool now clears the cache, re-runs
  discovery from scratch, and retries once. Recovers from a wrong-card
  cache without needing to delete `settings.ini` manually.
- **More multi-language Names** for the Battery & charging card:
  added Microsoft-newer terminology ("Smart charging", "Charging mode",
  "Battery Smart Charging") and additional locale entries.
- **Multi-language window title** when finding the Surface app process
  on launch. Was previously hardcoded to the English "Surface"; now
  accepts the localized window title for most major locales (de, ru,
  tr, ja, zh, ko, ar, etc.).
- **Multi-language Surface app DisplayName** in the package discovery
  scan. Was previously hardcoded English-only; now accepts any
  Surface-package-named entry regardless of localized display name.

### Diagnostics

- **UIA tree snapshot** is written to `surface-error.log` when the
  "card not found" or "no radio selected" errors fire. Captures the
  top 3 levels of the Surface app's UI tree (up to 60 elements) with
  each element's ControlType, Name, and AutomationId. Makes future
  bug reports actionable without needing the user to re-run anything.

### WMI brightness hardening

- Brightness values requested by simulated sleep are now **snapped to
  the closest level the display driver advertises** via
  `WmiMonitorBrightness.Levels`. Previously called `WmiSetBrightness(0)`
  unconditionally, which silently no-ops or fails on displays whose
  driver doesn't include `0` in its supported levels. Visible behavior
  unchanged on Surface built-in displays (which support 0); fixes a
  latent issue on certain external monitors / non-Surface laptops.

### Compatibility

Same OS / device requirements as v1.2.0. .NET 8 Desktop Runtime required.

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
