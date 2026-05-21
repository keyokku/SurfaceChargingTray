using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SurfaceChargingTray;

internal sealed class TrayAppContext : ApplicationContext
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
    private const uint ASFW_ANY = 0xFFFFFFFF;

    /// <summary>
    /// Asks Windows to release as many physical pages as possible from this
    /// process's working set. Our committed memory doesn't change — pages
    /// that haven't been touched recently get paged out, and Task Manager's
    /// "Memory" column drops correspondingly. Anything we use again is
    /// faulted back in on demand. Called after each operation completes
    /// and on an idle timer; the same trick OneDrive / Slack / Discord
    /// trays use to look slim in Task Manager.
    /// </summary>
    private static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
        catch { /* purely cosmetic — never let this crash */ }
    }

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _themeTimer;
    private readonly System.Windows.Forms.Timer _trimTimer;
    private readonly HotkeyManager _hotkeys = new();
    private readonly SynchronizationContext _ui;

    // Watches surface-state.json for changes from outside this process —
    // most importantly, scheduled-task CLI invocations that update the cache
    // while the tray is idle. Without this, a 7am scheduled "switch to 100%"
    // would leave the running tray showing the old mode until the user
    // clicked Refresh manually. Fired through _ui.Post so the menu update
    // hops back to the UI thread.
    private FileSystemWatcher? _stateWatcher;

    // Icons + bitmap loaded once and re-used. Re-creating them every theme
    // tick (5s) leaks HICON / HBITMAP handles until the next GC cycle and
    // drifts working set up over long uptimes.
    private readonly System.Drawing.Icon   _iconWhite;
    private readonly System.Drawing.Icon   _iconBlack;
    private readonly System.Drawing.Bitmap _errorBitmap;

    // Last theme we actually pushed; lets the timer no-op when nothing
    // changed (the common case).
    private bool? _appliedSystemDark;
    // Battery-health click → keep the menu open. Click handler sets this;
    // the menu's Closing handler honors it once and resets.
    private bool _suppressMenuClose = false;
    // Tracks the last applied tray icon by a string key so we can short-circuit
    // ApplyTrayIcon when nothing actually changed. Considers BOTH system theme
    // AND current charging mode now that v1.4.0 swaps colored mode icons in
    // addition to the dark/light plug fallback.
    private string _lastAppliedIconKey = "";
    // Last reported variant B state from OneShotStateWatcher. Cached so
    // ApplyTrayIcon can compute the right icon without re-querying the
    // watcher (and to handle the brief window between construction and
    // first state report).
    private OneShotStateWatcher.ButtonState _lastVariantBState = OneShotStateWatcher.ButtonState.Unknown;
    private bool? _appliedAppsDark;

    private SettingsModel _settings;
    private string _lastError = "";
    private bool _busy = false;

    // Tracks which UI variant the menu is currently shaped for so RunRefresh
    // can detect post-detection variant flips and call ApplyVariantToMenu
    // to reshape the menu without an app restart. Initialized from settings
    // at construction; updated whenever ApplyVariantToMenu runs.
    private SurfaceUiVariant _currentVariant = SurfaceUiVariant.Unknown;

    // Variant B only. Owns the power/battery state machine that keeps the
    // _mi100OneShot menu item's enabled-state synchronized with the Surface
    // app's button without polling. Created in ApplyVariantToMenu when
    // variant transitions to B; disposed when variant flips away from B
    // (or app exits). Variant A users incur ZERO instantiation, ZERO
    // background timers, ZERO power-event subscriptions from this field.
    private OneShotStateWatcher? _oneShotWatcher;

    private readonly ToolStripMenuItem _miAdaptive;
    private readonly ToolStripMenuItem _mi80;
    private readonly ToolStripMenuItem _mi100Day;
    private readonly ToolStripMenuItem _mi100Week;
    // Variant B's lone action — Surface app's one-shot "Charge to 100%"
    // override button. Visible only when DetectedVariant=B; hidden in the
    // variant A and Unknown menu shapes. Click invokes the button via UIA.
    private readonly ToolStripMenuItem _mi100OneShot;
    // Schedule item — shows the saved time + mode in the label
    // (e.g. "Schedule: 21:00 — 80%" or "Schedule: (not set)") and opens
    // the Settings dialog directly on the Schedule tab.
    private readonly ToolStripMenuItem _miSchedule;
    private readonly ToolStripMenuItem _miPower;       // submenu parent
    private readonly ToolStripMenuItem _miPowerEff;
    private readonly ToolStripMenuItem _miPowerBal;
    private readonly ToolStripMenuItem _miPowerPerf;
    // Battery Health menu item (v1.4.0). Informational — disabled (not
    // clickable). Caption shows compact summary, full info on hover.
    // Cached 24h via SettingsModel.BatteryHealthCheckedAt; refresh kicked
    // off on first menu open after cache expiry.
    private readonly ToolStripMenuItem _miBatteryHealth;
    private readonly ToolStripMenuItem _miRefresh;
    private readonly ToolStripMenuItem _miOpenApp;
    private readonly ToolStripMenuItem _miSettings;
    private readonly ToolStripMenuItem _miShowError;
    private readonly ToolStripMenuItem _miExit;

    public TrayAppContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = SettingsModel.Load();

        // Cache icons once. Each Icon owns a native HICON; allocating fresh
        // ones every 5 seconds previously bled handles until GC ran.
        _iconWhite = Icons.PlugWhite();
        _iconBlack = Icons.PlugBlack();
        // ToBitmap() copies the pixels into a managed Bitmap. We dispose
        // the source Icon immediately so its HICON is released right away.
        using (var ico = Icons.ErrorRed())
            _errorBitmap = ico.ToBitmap();

        // Detect the installed Surface app's AUMID dynamically. Always
        // returns something; persists to settings.ini.
        SurfaceController.Aumid = AumidResolver.Resolve(_settings);

        // Hand the same settings object to SurfaceController so UiaCache can
        // read/write the auto-discovered AutomationId + Name caches across
        // launches without us having to plumb settings through every call.
        SurfaceController.Settings = _settings;

        _menu = new ContextMenuStrip { ShowImageMargin = true };
        _miAdaptive   = new ToolStripMenuItem("Adaptive",                (Image?)null, (s, e) => StartSetMode("adaptive"));
        _mi80         = new ToolStripMenuItem("Limit to 80%",            (Image?)null, (s, e) => StartSetMode("80"));
        _mi100Day     = new ToolStripMenuItem("Charge to 100% (1 day)",  (Image?)null, (s, e) => StartSetMode("100", "1day"));
        _mi100Week    = new ToolStripMenuItem("Charge to 100% (1 week)", (Image?)null, (s, e) => StartSetMode("100", "1week"));
        _mi100OneShot = new ToolStripMenuItem("Charge to 100%",          (Image?)null, (s, e) => StartTriggerOneShot())
        {
            // Hover tooltip on the menu item itself — only meaningful when
            // the item is greyed (Disabled state) but harmless when enabled
            // since most users won't hover an enabled clickable item.
            ToolTipText = "Already charging to 100% / no smart charge limit currently"
        };

        _miSchedule  = new ToolStripMenuItem("Schedule: (not set)",     (Image?)null, (s, e) => ShowSettings(openOnScheduleTab: true));

        _miPowerEff  = new ToolStripMenuItem("Best power efficiency",   (Image?)null, (s, e) => SetPower(PowerMode.Mode.Efficient));
        _miPowerBal  = new ToolStripMenuItem("Balanced",                (Image?)null, (s, e) => SetPower(PowerMode.Mode.Balanced));
        _miPowerPerf = new ToolStripMenuItem("Best performance",        (Image?)null, (s, e) => SetPower(PowerMode.Mode.Performance));
        _miPower     = new ToolStripMenuItem("Windows Power mode");
        _miPower.DropDownItems.AddRange(new ToolStripItem[] { _miPowerEff, _miPowerBal, _miPowerPerf });

        _miBatteryHealth = new ToolStripMenuItem("Battery health: (checking...)",
            (Image?)null, (s, e) =>
            {
                // Suppress the menu's auto-close (this is an informational
                // item — closing the menu would be jarring). The menu's
                // Closing handler honors this flag once and resets.
                _suppressMenuClose = true;
                // Click forces a fresh WMI read (bypasses 24h cache) so users
                // who want up-to-the-second data can refresh on demand. Result
                // updates both the menu caption and the hover tooltip.
                ForceBatteryHealthRefresh();
            });
        // Render cached value at construction so the menu doesn't show
        // "(checking...)" on every launch if we already have data.
        ApplyCachedBatteryHealth();

        _miRefresh   = new ToolStripMenuItem("Refresh status",          (Image?)null, (s, e) => StartRefresh());
        _miOpenApp   = new ToolStripMenuItem("Open Surface app",        (Image?)null, (s, e) => OpenSurfaceApp());
        _miSettings  = new ToolStripMenuItem("Settings...",             (Image?)null, (s, e) => ShowSettings());
        _miShowError = new ToolStripMenuItem("Show last error",         (Image?)null, (s, e) => ShowLastError());
        _miExit      = new ToolStripMenuItem("Exit",                    (Image?)null, (s, e) => ExitThread());

#if DEV_TEST_TRIGGERS
        // Dev-only test triggers for the v1.4.0 ErrorDialog log viewer.
        // Wrapped in #if so they NEVER ship in a release build — flag is
        // set only when csproj has <DefineConstants>DEV_TEST_TRIGGERS</DefineConstants>.
        var miTestGenericError = new ToolStripMenuItem("(dev) Test: generic error",   (Image?)null, (s, e) =>
            ReportError("Test error — this is a simulated generic error to exercise the ErrorDialog log viewer. The textbox below should show recent log entries."));
        var miTestDetectionError = new ToolStripMenuItem("(dev) Test: detection-failure error", (Image?)null, (s, e) =>
            ReportError("Battery & charging card not found — the Surface app's UI may not support automation on this device, or the card may have moved."));
        var miTestShowLastError = new ToolStripMenuItem("(dev) Test: open last-error dialog", (Image?)null, (s, e) => ShowLastError());
#endif

        // Layout: charging modes [---] Schedule, Power mode [---] secondary actions.
        // All items always exist in the menu; ApplyVariantToMenu toggles
        // visibility based on the detected variant. Variant A shows the
        // four mode items; variant B shows only _mi100OneShot; Unknown
        // hides all charging-specific items.
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _miAdaptive, _mi80, _mi100Day, _mi100Week,
            _mi100OneShot,
            new ToolStripSeparator(),
            _miSchedule,
            _miPower,
            _miBatteryHealth,
            new ToolStripSeparator(),
            _miRefresh, _miOpenApp, _miSettings, _miShowError, _miExit
        });

#if DEV_TEST_TRIGGERS
        // Dev test triggers appended at the bottom of the menu after _miExit
        // so they're visually separated from the real items.
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(miTestGenericError);
        _menu.Items.Add(miTestDetectionError);
        _menu.Items.Add(miTestShowLastError);
#endif

        // Hide the Power-mode submenu entirely on systems that don't expose
        // overlays (very old Windows builds, server SKUs).
        if (!PowerMode.IsSupported())
            _miPower.Visible = false;

        // v1.4.0: bump the menu's internal tooltip auto-pop delay to 30s
        // so the multi-line Battery Health hover doesn't time out before
        // the user can read it. Single call, applies to all menu-item
        // ToolTipTexts in this menu.
        ExtendMenuTooltipDuration(30_000);

        // Battery-health menu item should stay open when clicked (it's
        // informational; closing the menu would feel jarring). The Click
        // handler sets _suppressMenuClose; this Closing hook honors it
        // exactly once. ContextMenuStrip fires Click then Closing in that
        // order, so the flag is set in time.
        _menu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && _suppressMenuClose)
            {
                e.Cancel = true;
                _suppressMenuClose = false;
            }
        };

        // Tray menu open: variant B's watcher (if instantiated) takes the
        // opportunity to refresh the one-shot button state. No-op for
        // variant A (watcher is null). Cheap when the watcher decides to
        // skip per its 30s debounce.
        _menu.Opening += (_, _) =>
        {
            _oneShotWatcher?.OnMenuOpening();
            // Battery health refresh — only fires if cache > 24h old.
            // Cheap: just a timestamp check on hot path; the WMI probe
            // happens on a background Task.
            RefreshBatteryHealthIfStale();
        };

        // Fake-sleep safety watchdog (Phase 9): force-exit fires here with
        // a reason. Surface it as a balloon so the user knows simulated
        // sleep ended unexpectedly — and why. Variant-agnostic (both A and
        // B use the scheduler that runs through fake-sleep).
        FakeSleepMode.WatchdogExited += OnFakeSleepWatchdogExited;

        _icon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = "Surface Charging: ?",
            Visible = true
        };

        ApplyTrayIcon();
        ApplyMenuTheme();
        ApplyVariantToMenu();   // shapes the menu (variant A / B / Unknown) before first display
        UpdateMenuFromCache();
        UpdatePowerModeChecks();
        UpdateScheduleMenuLabel();
        ApplyHotkeys();

        // Auto-discovery on first launch: fire a background RefreshState if
        // either (a) the UIA cache is empty (fresh install) or (b) the
        // variant hasn't been classified yet (v1.2.x -> v1.3.0 upgraders).
        // RefreshState's normal-then-discovery flow expands the Surface app's
        // UI, writes every needed AutomationId and Name into settings.ini,
        // and — new in v1.3.0 — runs DetectVariant so the menu reshapes
        // itself before the user clicks anything. The DetectedVariant guard
        // is critical for v1.2.x upgraders on variant B devices: their
        // BatteryCardId is already cached (v1.2.x's Layer 3 Name match
        // worked) but they need a fresh detection pass to discover they're
        // on variant B and reshape the menu accordingly.
        if (string.IsNullOrEmpty(_settings.BatteryCardId)
         || string.IsNullOrEmpty(_settings.DetectedVariant)) StartRefresh();

        // Watch surface-state.json for external writes — primarily by
        // scheduled-task CLI invocations of our exe. When they update the
        // cache, refresh the tray menu's check marks immediately so the
        // user never sees a stale state after a scheduled mode change.
        try
        {
            var dir = Path.GetDirectoryName(StateStore.CachePath);
            var name = Path.GetFileName(StateStore.CachePath);
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(name))
            {
                _stateWatcher = new FileSystemWatcher(dir, name)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler handler = (_, _) =>
                    _ui.Post(_ =>
                    {
                        try { UpdateMenuFromCache(); } catch { }
                        // Refresh tray icon — v1.4.0+ swaps colored mode
                        // icons when StateStore changes (variant A path).
                        try { ApplyTrayIcon(); } catch { }
                    }, null);
                _stateWatcher.Changed += handler;
                _stateWatcher.Created += handler;
            }
        }
        catch { /* watcher is a nice-to-have; tray still works without it */ }

        _themeTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _themeTimer.Tick += (s, e) =>
        {
            ApplyTrayIcon();
            ApplyMenuTheme();
            // Cheap (one Win32 call) — keeps Power-mode check marks in sync
            // when the user changes it via Settings, or when Surface
            // auto-switches on AC/DC transitions.
            UpdatePowerModeChecks();
        };
        _themeTimer.Start();

        // Periodic working-set trim while idle so Task Manager stays slim
        // even after a stretch of activity. 5 min is conservative; the OS
        // would do this on its own under memory pressure, we just don't
        // wait for it.
        // Also hosts the v1.4.0 calibration-reminder check — cheap
        // (SystemInformation.PowerStatus read + a few timestamp compares)
        // and the 5-min cadence is fine for catching the noon-ish window
        // (11:00-14:00) when the user-facing toast should fire.
        _trimTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        _trimTimer.Tick += (s, e) =>
        {
            if (!_busy) TrimWorkingSet();
            CheckCalibrationReminder();
        };
        _trimTimer.Start();

        // Initial trim after construction completes — one-time pages used
        // for JIT during startup aren't needed anymore.
        TrimWorkingSet();

        // Seed LastFullChargeAt with "now" on first run so we have a
        // baseline for the 30-day calibration reminder. Without this,
        // users who install fresh and never reach 100% would never get
        // the reminder (no baseline to measure 30 days from).
        if (string.IsNullOrEmpty(_settings.LastFullChargeAt))
        {
            _settings.LastFullChargeAt = DateTime.UtcNow.ToString("o");
            _settings.Save();
        }

        // Kick off the GitHub releases update check in the background.
        // Throttled to once/24h via settings; silent on failure. If a
        // newer release is found, a balloon notification appears with
        // a one-shot click handler to open the releases page.
        KickOffUpdateCheck();
    }

    // ---- Mode switching ------------------------------------------------
    //
    // Each user action runs serially against the Surface app — back-to-back
    // requests would race the Surface app's launch/teardown cycle and fail
    // unpredictably. Instead of dropping rapid clicks (silent and confusing),
    // we coalesce them: while one operation is in flight, the most recent
    // pending request is held in _pendingMode / _pendingRefresh, and the
    // completion handler kicks it off automatically. Only the LATEST mode
    // wins on rapid-fire clicks — the user's actual intent — and they get
    // visible feedback ("queued: …") instead of a silently-dropped click.

    private (string mode, string? duration)? _pendingMode = null;
    private bool _pendingRefresh = false;
    // Variant B's queued action — analog of _pendingMode, but the action is
    // parameter-free so a bool suffices. Set when StartTriggerOneShot is
    // called while _busy; consumed by ProcessPending.
    private bool _pendingOneShot = false;

    private void StartSetMode(string mode, string? duration = null)
    {
        if (_busy)
        {
            _pendingMode = (mode, duration);
            // Refresh queued behind a mode change is redundant — the mode
            // change reads + saves state on its own. Drop any pending refresh.
            _pendingRefresh = false;
            _icon.Text = ClampTooltip("Surface Charging: queued " + LabelFor(mode, duration));
            return;
        }
        RunSetMode(mode, duration);
    }

    private void RunSetMode(string mode, string? duration)
    {
        _busy = true;
        _icon.Text = "Surface Charging: switching...";

        Task.Run(() =>
        {
            var err = SurfaceController.SetMode(mode, duration);
            _ui.Post(_ =>
            {
                _busy = false;
                if (err != null) ReportError(err);
                else { ClearError(); UpdateMenuFromCache(); }
                TrimWorkingSet();   // shake off the transient allocations
                ProcessPending();
            }, null);
        });
    }

    /// <summary>Run the queued operation if any, after a previous one finished.</summary>
    private void ProcessPending()
    {
        if (_pendingMode.HasValue)
        {
            var (m, d) = _pendingMode.Value;
            _pendingMode = null;
            _pendingRefresh = false;   // mode change supersedes any pending refresh
            _pendingOneShot = false;   // unreachable in practice (different variants) but defensive
            RunSetMode(m, d);
        }
        else if (_pendingOneShot)
        {
            _pendingOneShot = false;
            _pendingRefresh = false;
            RunTriggerOneShot();
        }
        else if (_pendingRefresh)
        {
            _pendingRefresh = false;
            RunRefresh();
        }
    }

    // ---- Variant B: one-shot override --------------------------------
    //
    // Mirrors StartSetMode / RunSetMode but uses SurfaceController.TriggerOneShot
    // (variant B's only action). No mode / duration; the action is parameter-
    // free. Queued via _pendingOneShot when fired during _busy. Variant A
    // users never enter these paths — _mi100OneShot is hidden for them by
    // ApplyVariantToMenu, and CycleMode only routes here when DetectedVariant=B.

    private void StartTriggerOneShot()
    {
        if (_busy)
        {
            _pendingOneShot = true;
            _pendingRefresh = false;   // one-shot reads/clears state itself
            _icon.Text = ClampTooltip("Surface Charging: queued Charge to 100%");
            return;
        }
        RunTriggerOneShot();
    }

    private void RunTriggerOneShot()
    {
        _busy = true;
        _icon.Text = "Surface Charging: triggering...";

        Task.Run(() =>
        {
            var err = SurfaceController.TriggerOneShot();
            _ui.Post(_ =>
            {
                _busy = false;
                if (err != null) ReportError(err);
                else
                {
                    ClearError();
                    // Variant B has no StateStore mirror. The watcher fully
                    // owns the tray tooltip (post-invoke it sets the disabled
                    // state's tooltip via OnOneShotStateChanged). Microsoft
                    // greys the button right after invocation, so notifying
                    // the watcher immediately is reliable — no probe needed
                    // for this transition.
                    _oneShotWatcher?.NotifyButtonInvoked();
                }
                TrimWorkingSet();
                ProcessPending();
            }, null);
        });
    }

    // ---- Variant-aware menu shaping ----------------------------------
    //
    // Toggles visibility of menu items based on the detected Surface UI
    // variant. Called at construction (so the very first display has the
    // right shape) and after every RunRefresh that flips the cached
    // variant. All items live in the menu permanently; visibility is the
    // only thing that changes — cheaper and less error-prone than tearing
    // down + reconstructing items.

    private static SurfaceUiVariant ParseVariant(string? s) => s switch
    {
        "A" => SurfaceUiVariant.A,
        "B" => SurfaceUiVariant.B,
        _   => SurfaceUiVariant.Unknown
    };

    private void ApplyVariantToMenu()
    {
        // Treat first-launch (DetectedVariant null) as variant A — the
        // overwhelmingly common case for existing v1.2.x users upgrading.
        // After the first RefreshState's silent detection writes the real
        // variant to settings.ini, this method runs again with the right
        // value and the menu adjusts if needed.
        var v = ParseVariant(_settings.DetectedVariant);
        if (v == SurfaceUiVariant.Unknown && string.IsNullOrEmpty(_settings.DetectedVariant))
            v = SurfaceUiVariant.A;

        _currentVariant = v;

        bool isA = v == SurfaceUiVariant.A;
        bool isB = v == SurfaceUiVariant.B;

        _miAdaptive.Visible   = isA;
        _mi80.Visible         = isA;
        _mi100Day.Visible     = isA;
        _mi100Week.Visible    = isA;
        _mi100OneShot.Visible = isB;

        // Schedule item: visible for A and B (Phase 5 makes the dialog's
        // schedule section variant-aware). Hidden for Unknown — no
        // actionable target to schedule.
        _miSchedule.Visible = isA || isB;

        // ---- Variant B watcher lifecycle (Phase 8) ----
        // Created only when transitioning into B; disposed when transitioning
        // out of B. Variant A users never instantiate it: zero background
        // timers, zero power-event subscriptions, zero Surface-app probes
        // from this code path.
        if (isB && _oneShotWatcher == null)
        {
            _oneShotWatcher = new OneShotStateWatcher(_ui, OnOneShotProbeRequested);
            _oneShotWatcher.StateChanged += OnOneShotStateChanged;
            _oneShotWatcher.Initialize();   // fire initial state report
        }
        else if (!isB && _oneShotWatcher != null)
        {
            _oneShotWatcher.StateChanged -= OnOneShotStateChanged;
            _oneShotWatcher.Dispose();
            _oneShotWatcher = null;
            // Restore default menu-item appearance — if we ever flip back
            // to B later, the watcher rebuilds its state from scratch.
            _mi100OneShot.Enabled = true;
            _mi100OneShot.Text    = "Charge to 100%";
        }
    }

    /// <summary>
    /// Called by OneShotStateWatcher when it wants to probe the Surface app's
    /// one-shot button enabled-state. Serializes against other Surface-app
    /// interactions via _busy, exactly like StartTriggerOneShot / StartRefresh.
    /// Result is delivered on the UI thread (the callback is invoked there).
    /// </summary>
    private void OnOneShotProbeRequested(Action<bool?> onResult)
    {
        if (_busy)
        {
            // Don't queue; the watcher will re-trigger on next opportunity
            // (next battery transition / menu open). Probes are best-effort.
            onResult(null);
            return;
        }
        _busy = true;
        Task.Run(() =>
        {
            bool? r = SurfaceController.ProbeOneShotEnabled();
            _ui.Post(_ =>
            {
                _busy = false;
                TrimWorkingSet();
                ProcessPending();
                try { onResult(r); } catch { }
            }, null);
        });
    }

    /// <summary>
    /// Called on the UI thread when the watcher's reported state changes.
    /// Updates the variant B menu item's enabled state, label, and the
    /// tray tooltip.
    /// </summary>
    /// <summary>
    /// Fires (on UI thread) when FakeSleepMode's safety watchdog force-exits.
    /// Surface the reason to the user via a balloon notification so they know
    /// simulated sleep ended unexpectedly AND why. Also stamp the error log
    /// so the reason is preserved for diagnostics if the balloon is missed.
    /// </summary>
    private void OnFakeSleepWatchdogExited(string reason)
    {
        try
        {
            ClearError();
            _icon.ShowBalloonTip(
                10_000,
                "Simulated sleep ended",
                reason.Length > 250 ? reason[..250] : reason,
                ToolTipIcon.Warning);
            _icon.Text = ClampTooltip("Surface Charging — simulated sleep exited (see log)");
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] TrayAppContext: OnFakeSleepWatchdogExited balloon: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnOneShotStateChanged(OneShotStateWatcher.ButtonState s)
    {
        // Cache for the tray-icon swap (ApplyTrayIcon reads this).
        _lastVariantBState = s;
        // Menu item text never changes — only the Enabled (greyed) state.
        // Standard Windows UX: disabled items aren't relabeled. The hover
        // tooltip on the menu item (set at construction) explains why.
        // Tray icon hover tooltip carries the at-a-glance state.
        _mi100OneShot.Text = "Charge to 100%";
        switch (s)
        {
            case OneShotStateWatcher.ButtonState.Enabled:
                // Smart Charging is actively limiting (typically holding at 80%).
                _mi100OneShot.Enabled = true;
                _icon.Text = ComposeTooltip("Surface Charging: Smart");
                break;
            case OneShotStateWatcher.ButtonState.Disabled:
                // Either override already triggered (going to 100%) or Smart
                // Charging not limiting (also going to 100%). Either way the
                // device is on the path to 100%.
                _mi100OneShot.Enabled = false;
                _icon.Text = ComposeTooltip("Surface Charging: To 100%");
                break;
            case OneShotStateWatcher.ButtonState.Unknown:
                // Initial / indeterminate state. Render as clickable; if the
                // user clicks, TriggerOneShot's IsEnabled guard will surface
                // a clean error if it turns out the button is disabled.
                _mi100OneShot.Enabled = true;
                _icon.Text = ComposeTooltip("Surface Charging");
                break;
        }
        // Refresh tray icon so the colored mode icon swaps with the state.
        ApplyTrayIcon();
    }

    private void StartRefresh()
    {
        if (_busy)
        {
            // Multiple rapid refreshes coalesce into one — running back-to-back
            // refreshes would just cycle the Surface app for no benefit.
            _pendingRefresh = true;
            return;
        }
        RunRefresh();
    }

    private void RunRefresh()
    {
        _busy = true;
        _icon.Text = "Surface Charging: refreshing...";

        Task.Run(() =>
        {
            var err = SurfaceController.RefreshState();
            _ui.Post(_ =>
            {
                _busy = false;
                if (err != null) ReportError(err);
                else             { ClearError(); UpdateMenuFromCache(); }

                // Variant may have flipped after the DetectVariant call
                // inside RefreshState — reshape the menu if so. Cheap (a
                // few bool assignments); no-op when the variant hasn't
                // changed. Also re-apply hotkeys, since variant-specific
                // hotkey filtering depends on _currentVariant.
                if (ParseVariant(_settings.DetectedVariant) != _currentVariant)
                {
                    ApplyVariantToMenu();
                    ApplyHotkeys();
                }

                // Refresh power mode too — cheap (one Win32 call).
                UpdatePowerModeChecks();
                TrimWorkingSet();
                ProcessPending();
            }, null);
        });
    }

    // ---- Power mode -----------------------------------------------------

    private void SetPower(PowerMode.Mode mode)
    {
        if (PowerMode.Set(mode))
            UpdatePowerModeChecks();
        else
            ReportError($"Failed to set Windows Power mode to {PowerMode.Label(mode)}.");
    }

    private void UpdatePowerModeChecks()
    {
        var current = PowerMode.Get();
        _miPowerEff.Checked  = current == PowerMode.Mode.Efficient;
        _miPowerBal.Checked  = current == PowerMode.Mode.Balanced;
        _miPowerPerf.Checked = current == PowerMode.Mode.Performance;
    }

    // ---- Error surfacing -----------------------------------------------

    private void ReportError(string msg)
    {
        _lastError = msg;
        _icon.Text = ClampTooltip("Surface Charging: ERROR — right-click 'Show last error'");
        // Reuse the cached error bitmap; never re-allocate.
        try { _miShowError.Image = _errorBitmap; } catch { }
        _icon.ShowBalloonTip(5000, "Surface charging tray", msg.Length > 200 ? msg[..200] : msg, ToolTipIcon.Error);
    }

    private void ClearError()
    {
        _lastError = "";
        // Don't dispose _errorBitmap — it's a cached resource we may need
        // again. Just unlink it from the menu item.
        try { _miShowError.Image = null; } catch { }
    }

    private void ShowLastError()
    {
        if (string.IsNullOrEmpty(_lastError))
        {
            MessageBox.Show("No errors recorded. Last action succeeded.",
                "Surface tray", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            // ErrorDialog adds clickable links to the diagnostic tool +
            // GitHub thread when the message matches a detection-failure
            // pattern; falls back to a plain error display otherwise.
            ErrorDialog.Show(null, _lastError);
        }
    }

    // ---- Tray icon + menu state ---------------------------------------

    private void ApplyTrayIcon()
    {
        bool dark = DarkMode.IsSystemDarkMode();
        _appliedSystemDark = dark;

        // v1.4.0+ chooses among colored mode icons (Adaptive/80%/100%) when
        // we know the current state, falling back to the dark/light plug
        // icon when state is unknown or the app is in an error state.
        string key = ComputeTrayIconKey(dark);
        if (key == _lastAppliedIconKey) return;
        _lastAppliedIconKey = key;

        try
        {
            _icon.Icon = key switch
            {
                "adaptive-dark"  => IconBuilder.Get(IconBuilder.Badge.Adaptive,  true),
                "adaptive-light" => IconBuilder.Get(IconBuilder.Badge.Adaptive,  false),
                "80-dark"        => IconBuilder.Get(IconBuilder.Badge.Limit80,   true),
                "80-light"       => IconBuilder.Get(IconBuilder.Badge.Limit80,   false),
                "100-dark"       => IconBuilder.Get(IconBuilder.Badge.Charge100, true),
                "100-light"      => IconBuilder.Get(IconBuilder.Badge.Charge100, false),
                "dark-plug"      => _iconWhite,
                _                => _iconBlack
            };
        }
        catch { }
    }

    /// <summary>
    /// Resolves the current charging state to a tray-icon key. Keys map to
    /// either a badged mode icon (adaptive/80/100) or the dark/light plug
    /// fallback. The mode keys are theme-suffixed (e.g. "adaptive-dark")
    /// because we cache one variant per theme (the badged icon overlays
    /// the plug, and the plug differs between dark/light system tray).
    /// Error state forces the plug fallback so the user can still see
    /// the icon-with-error-overlay via the menu's 'Show last error'.
    /// </summary>
    private string ComputeTrayIconKey(bool dark)
    {
        string plug = dark ? "dark-plug" : "light-plug";
        if (!string.IsNullOrEmpty(_lastError)) return plug;
        string suffix = dark ? "-dark" : "-light";

        if (_currentVariant == SurfaceUiVariant.A)
        {
            var st = StateStore.Load();
            return st.Mode switch
            {
                "adaptive" => "adaptive" + suffix,
                "80"       => "80"       + suffix,
                "100"      => "100"      + suffix,
                _          => plug
            };
        }
        if (_currentVariant == SurfaceUiVariant.B)
        {
            return _lastVariantBState switch
            {
                // Smart Charging engaged = adaptive-style green squiggle
                OneShotStateWatcher.ButtonState.Enabled  => "adaptive" + suffix,
                // Override active OR not currently limiting = "going to 100%"
                OneShotStateWatcher.ButtonState.Disabled => "100"      + suffix,
                _                                         => plug
            };
        }
        return plug;
    }

    private void ApplyMenuTheme()
    {
        bool dark = DarkMode.IsAppsDarkMode();
        if (_appliedAppsDark == dark) return;
        _appliedAppsDark = dark;
        DarkMenu.ApplyTo(_menu);
    }

    private void UpdateMenuFromCache()
    {
        var st = StateStore.Load();

        // Tooltip: variant B has no persistent "current mode" — show a
        // simple label. Variant A and Unknown keep the v1.2.x format.
        string line1;
        if (_currentVariant == SurfaceUiVariant.B)
            line1 = "Surface Charging";
        else
            line1 = "Surface Charging: " + LabelFor(st.Mode, st.Duration);
        _icon.Text = ComposeTooltip(line1);

        // Variant A mode-item check marks. Updating these for variant B
        // is a harmless no-op (the items are Visible=false) but kept
        // unconditional to keep the code path identical when the variant
        // flips mid-session.
        _miAdaptive.Checked = st.Mode == "adaptive";
        _mi80.Checked       = st.Mode == "80";
        _mi100Day.Checked   = st.Mode == "100" && st.Duration == "1day";
        _mi100Week.Checked  = st.Mode == "100" && st.Duration == "1week";

        // Keep the tray icon in sync with the (possibly changed) mode.
        // No-op via ApplyTrayIcon's key-equality short-circuit if nothing
        // actually changed since last call.
        ApplyTrayIcon();
    }

    /// <summary>
    /// Composes the tray-icon tooltip with the new v1.4.0 second line
    /// showing the current Windows Power mode. NotifyIcon.Text is limited
    /// to 127 chars on Win11; ClampTooltip enforces.
    /// </summary>
    private string ComposeTooltip(string firstLine)
    {
        var pm = PowerMode.Get();
        if (pm == PowerMode.Mode.Unknown) return ClampTooltip(firstLine);
        return ClampTooltip(firstLine + "\n" + "Power mode: " + PowerMode.Label(pm));
    }

    // ---- Battery health (v1.4.0) ---------------------------------------

    /// <summary>
    /// Populate the menu item caption + hover tooltip from whatever's in
    /// settings.ini at construction. Idempotent — also called after a fresh
    /// async read completes.
    /// </summary>
    private void ApplyCachedBatteryHealth()
    {
        _miBatteryHealth.Text = string.IsNullOrEmpty(_settings.BatteryHealthSummary)
            ? "Battery health: (checking...)"
            : _settings.BatteryHealthSummary;
        _miBatteryHealth.ToolTipText = _settings.BatteryHealthTooltip ?? "";
    }

    /// <summary>
    /// If the cache is older than 24h (or empty), schedule a background
    /// WMI read. Writes the result back to settings.ini + updates the
    /// menu item. Safe to call on every menu open; cheap when cached.
    /// </summary>
    private void RefreshBatteryHealthIfStale()
    {
        bool stale = string.IsNullOrEmpty(_settings.BatteryHealthCheckedAt)
                  || !DateTime.TryParse(_settings.BatteryHealthCheckedAt, out var lastAt)
                  || (DateTime.UtcNow - lastAt.ToUniversalTime()).TotalHours >= 24;
        if (stale) RunBatteryHealthRead();
    }

    /// <summary>
    /// Click handler — bypasses the 24h cache and forces an immediate
    /// re-read. Used when the user wants up-to-the-second data instead
    /// of the day-old cache.
    /// </summary>
    private void ForceBatteryHealthRefresh() => RunBatteryHealthRead();

    private void RunBatteryHealthRead()
    {
        Task.Run(() =>
        {
            BatteryHealthReader.Result? r = null;
            try { r = BatteryHealthReader.Read(); }
            catch (Exception ex)
            {
                try { Logger.Error($"[INFO] BatteryHealthReader: {ex.GetType().Name}: {ex.Message}"); } catch { }
            }
            if (r == null) return;
            _ui.Post(_ =>
            {
                try
                {
                    _settings.BatteryHealthSummary   = r.Summary;
                    _settings.BatteryHealthTooltip   = r.Tooltip;
                    _settings.BatteryHealthCheckedAt = DateTime.UtcNow.ToString("o");
                    _settings.Save();
                    ApplyCachedBatteryHealth();
                }
                catch { }
            }, null);
        });
    }

    /// <summary>
    /// Extends the ContextMenuStrip's internal ToolTip AutoPopDelay so the
    /// Battery Health hover tooltip (multi-line, takes a moment to read)
    /// doesn't time out in the default 5 seconds. Uses reflection because
    /// ToolStrip.toolTip is internal — wrapped in try/catch so any future
    /// WinForms internal rename degrades to the default duration silently.
    /// </summary>
    private void ExtendMenuTooltipDuration(int autoPopMs)
    {
        try
        {
            var fld = typeof(System.Windows.Forms.ToolStrip).GetField("toolTip",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (fld?.GetValue(_menu) is System.Windows.Forms.ToolTip tt)
            {
                tt.AutoPopDelay = autoPopMs;
                tt.InitialDelay = 500;
                tt.ReshowDelay  = 200;
            }
        }
        catch (Exception ex)
        {
            try { Logger.Error($"[INFO] ExtendMenuTooltipDuration: {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    // ---- Calibration reminder (v1.4.0) ---------------------------------

    /// <summary>
    /// Tracks the last observed battery percent so we can detect the
    /// transition into 100% (only fires the reminder reset once per cycle,
    /// not on every tick at 100%).
    /// </summary>
    private int _calibLastBatteryPct = -1;

    /// <summary>
    /// Called from the _trimTimer tick (~5 min). Updates LastFullChargeAt
    /// when battery hits 100%, and fires a one-time toast notification if
    /// 30 days have elapsed since the last full charge and current local
    /// time is in the 11:00-14:00 window (so the user is likely active
    /// and the notification isn't lost at 3am).
    /// </summary>
    private void CheckCalibrationReminder()
    {
        try
        {
            var ps = SystemInformation.PowerStatus;
            float pf = ps.BatteryLifePercent;
            if (float.IsNaN(pf) || pf < 0) return;
            int pct = (int)Math.Round(Math.Min(pf, 1f) * 100);
            if (pct < 0 || pct > 100) return;

            // Detect transition to 100% (charging complete this cycle).
            if (_calibLastBatteryPct >= 0 && _calibLastBatteryPct < 100 && pct >= 100)
            {
                _settings.LastFullChargeAt = DateTime.UtcNow.ToString("o");
                _settings.CalibrationReminderShown = false;
                _settings.Save();
            }
            _calibLastBatteryPct = pct;

            // Reminder fired this cycle already — wait for next full charge to reset.
            if (_settings.CalibrationReminderShown) return;
            if (string.IsNullOrEmpty(_settings.LastFullChargeAt)) return;
            if (!DateTime.TryParse(_settings.LastFullChargeAt, out var lastFull)) return;

            var daysSince = (DateTime.UtcNow - lastFull.ToUniversalTime()).TotalDays;
            if (daysSince < 30) return;

            // Noon-ish window so the toast is seen by an active user.
            var hour = DateTime.Now.Hour;
            if (hour < 11 || hour > 14) return;

            _settings.CalibrationReminderShown = true;
            _settings.Save();
            _icon.ShowBalloonTip(15_000,
                "Battery calibration suggestion",
                "Your battery hasn't reached 100% in over 30 days. A full 0 -> 100% cycle helps the fuel gauge stay accurate. " +
                "Consider switching Smart Charging to 'Adaptive' or 'Charge to 100%' temporarily.",
                ToolTipIcon.Info);
        }
        catch { }
    }

    // ---- Update check (v1.4.0) -----------------------------------------

    /// <summary>
    /// Fired once, near the end of construction. Async, non-blocking.
    /// Hits the GitHub Releases API (throttled to once/24h via settings),
    /// pops a tray balloon if a newer release tag is found. Click on the
    /// balloon opens the releases page. Silent on failure — update check
    /// is opportunistic.
    /// </summary>
    private void KickOffUpdateCheck()
    {
        Task.Run(async () =>
        {
            try
            {
                var v = typeof(TrayAppContext).Assembly.GetName().Version?.ToString() ?? "0.0.0";
                var r = await UpdateChecker.CheckAsync(_settings, v).ConfigureAwait(false);
                if (r?.UpdateAvailable != true) return;

                _ui.Post(_ =>
                {
                    try
                    {
                        _icon.ShowBalloonTip(15_000,
                            "Update available",
                            $"{r.LatestTag} is now available. Click to view release notes.",
                            ToolTipIcon.Info);

                        // Wire a single-use click handler that opens the
                        // releases page. Unsubscribe immediately on click
                        // so we don't accumulate handlers across multiple
                        // balloon shows.
                        EventHandler? clickHandler = null;
                        clickHandler = (_, _) =>
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.ReleasesUrl)
                                {
                                    UseShellExecute = true
                                });
                            }
                            catch { }
                            if (clickHandler != null) _icon.BalloonTipClicked -= clickHandler;
                        };
                        _icon.BalloonTipClicked += clickHandler;
                    }
                    catch { }
                }, null);
            }
            catch { }
        });
    }

    private static string LabelFor(string? mode, string? duration) => mode switch
    {
        "adaptive" => "Adaptive",
        "80"       => "Limit to 80%",
        "100" when duration == "1day"  => "Charge to 100% (1 day)",
        "100" when duration == "1week" => "Charge to 100% (1 week)",
        "100"      => "Charge to 100%",
        _          => "?"
    };

    /// <summary>NotifyIcon.Text has a 127-char limit; clip just in case.</summary>
    // NotifyIcon.Text limit: 64 chars on XP (legacy), 128 chars on Vista+
    // (Win10/11 firmly in the 128-char era). We cap at 127 to leave room
    // for the null terminator. Old 63-char cap was truncating the two-line
    // tooltip mid-word ("Best power efficiency" → "Best power").
    private static string ClampTooltip(string s) => s.Length > 127 ? s[..127] : s;

    // ---- Simulated-sleep scheduler ------------------------------------

    /// <summary>
    /// Enter the fake-sleep test: dim the screen, drop into Best power
    /// efficiency, prevent the system/display from sleeping, and show
    /// a fullscreen black overlay on every monitor. Dismissed by any
    /// mouse click or key press inside the overlay — mouse-move alone
    /// will NOT dismiss it (pen/cat-drift safety).
    ///
    /// This is the prototype that replaces the scheduled-wake approach
    /// (which can't drive the Surface app while UWP defers rendering on
    /// a sleeping / screen-off / locked device).
    /// </summary>
    /// <summary>
    /// Hotkey handler for "schedule-toggle". Acts as on/off for the
    /// simulated-sleep scheduler:
    ///   active  -> exit (manual dismiss equivalent)
    ///   not active -> read schedule from settings, compute delay until the
    ///                 next occurrence of ScheduleTime, enter simulated sleep
    ///                 with that scheduled fire. If no ScheduleTime is set,
    ///                 enter simulated sleep without a scheduled fire (the
    ///                 hotkey doubles as a "do not disturb" toggle).
    /// </summary>
    private void ToggleScheduledFakeSleep()
    {
        if (FakeSleepMode.IsActive)
        {
            FakeSleepMode.Exit();
            return;
        }
        EnterScheduledFakeSleep();
    }

    private void EnterScheduledFakeSleep()
    {
        // Re-load settings — the user may have changed the schedule between
        // tray launch and now.
        var s = SettingsModel.Load();

        // Build one ScheduledFire per configured slot, each with its delay
        // to the next occurrence of its time. Skip slots whose time can't be
        // parsed or whose delay is 0.
        var fires = new List<ScheduledFire>();
        foreach (var slot in s.Schedules)
        {
            if (string.IsNullOrEmpty(slot.Time) || string.IsNullOrEmpty(slot.Mode)) continue;
            int delay = SecondsUntilNextOccurrence(slot.Time);
            if (delay <= 0) continue;

            // Variant-B schedule: Mode='oneshot' (no duration). Variant-A:
            // Mode is 'adaptive'/'80'/'100' with optional Duration. The action
            // subtype encapsulates which SurfaceController call fires.
            ScheduledAction action = slot.Mode == "oneshot"
                ? new TriggerOneShotAction()
                : new SetModeAction { Mode = slot.Mode, Duration = slot.Duration };

            fires.Add(new ScheduledFire { Action = action, DelaySeconds = delay });
        }

        string? err;
        try
        {
            err = FakeSleepMode.Enter(fires, s.ScheduleAutoExit);
        }
        catch (Exception ex)
        {
            ReportError("Could not enter simulated sleep: " + ex.Message);
            return;
        }
        if (err != null)
        {
            // Use a modal dialog rather than a balloon-tip / toast — Windows
            // Focus Assist (Do Not Disturb) suppresses NotifyIcon balloons,
            // and refusing to enter fake-sleep silently is a worse failure
            // than the brief interruption a MessageBox causes.
            MessageBox.Show(err, "Surface charging tray",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Given a "HH:MM" target time, return the number of seconds until the
    /// NEXT occurrence of that wall-clock time. If the time has already
    /// passed today, returns the delay until tomorrow at that time.
    /// </summary>
    private static int SecondsUntilNextOccurrence(string hhmm)
    {
        var parts = hhmm.Split(':');
        if (parts.Length != 2) return 0;
        if (!int.TryParse(parts[0], out int hh) || !int.TryParse(parts[1], out int mm))
            return 0;
        if (hh < 0 || hh > 23 || mm < 0 || mm > 59) return 0;

        var now = DateTime.Now;
        var target = new DateTime(now.Year, now.Month, now.Day, hh, mm, 0);
        if (target <= now) target = target.AddDays(1);
        var span = target - now;
        return (int)Math.Min(span.TotalSeconds, int.MaxValue);
    }

    // ---- Other actions -------------------------------------------------

    private static void OpenSurfaceApp()
    {
        try { UwpLauncher.Launch(SurfaceController.Aumid); }
        catch { }
    }

    private void ShowSettings(bool openOnScheduleTab = false)
    {
        try
        {
            using var form = new SettingsForm(_settings, openOnScheduleTab);
            form.Saved = () =>
            {
                _settings = SettingsModel.Load();
                // Re-shape the menu in case the dialog's "Re-detect device"
                // button (Phase 4) flipped DetectedVariant on disk. ApplyHotkeys
                // also runs because variant-B hotkey rows differ from variant-A.
                ApplyVariantToMenu();
                UpdateMenuFromCache();   // refresh tooltip for the new variant
                ApplyHotkeys();
                UpdateScheduleMenuLabel();
                _icon.ShowBalloonTip(2000, "Surface charging tray", "Settings updated.", ToolTipIcon.Info);
            };
            form.ShowDialog();
            TrimWorkingSet();   // WPF/WinForms layout pages aren't needed after close
        }
        catch (Exception ex)
        {
            ReportError("Settings dialog crashed: " + ex);
        }
    }

    /// <summary>
    /// Refresh the tray menu's "Schedule: ..." label to reflect what's
    /// currently saved in settings. Called on construction and after every
    /// Settings save. No live count-down — the label shows the saved
    /// configuration; the actual next-occurrence math happens when the
    /// schedule-toggle hotkey is pressed.
    /// </summary>
    private void UpdateScheduleMenuLabel()
    {
        _miSchedule.Text = "Schedule: " + FormatScheduleLabel(_settings);
    }

    private static string FormatScheduleLabel(SettingsModel s)
    {
        if (s.Schedules.Count == 0) return "(not set)";

        // Single slot: full "HH:MM — mode" label (as in v1.2.x-v1.3.x).
        if (s.Schedules.Count == 1)
            return FormatSlotLabel(s.Schedules[0]);

        // Multiple slots: compact "HH:MM, HH:MM, HH:MM (N modes)" — the full
        // detail lives in the Settings → Schedule tab; the menu just shows
        // the times so the user knows it's armed and when.
        var times = string.Join(", ", s.Schedules
            .OrderBy(e => e.Time, StringComparer.Ordinal)
            .Select(e => e.Time));
        return $"{times} ({s.Schedules.Count} slots)";
    }

    private static string FormatSlotLabel(SettingsModel.ScheduleEntry e)
    {
        string modeText = e.Mode switch
        {
            "adaptive" => "Adaptive",
            "80"       => "80%",
            "100" when e.Duration == "1week" => "100% 1w",
            "100" when e.Duration == "1day"  => "100% 1d",
            "100"      => "100%",
            "oneshot"  => "100% override",
            _          => e.Mode
        };
        return $"{e.Time} — {modeText}";  // em-dash separator
    }

    // ---- Hotkeys -------------------------------------------------------

    /// <summary>
    /// Hotkey callbacks run inside the WM_HOTKEY WndProc, which is the
    /// brief window during which Windows considers our process as having
    /// "received user input" and therefore eligible to set foreground.
    /// We immediately grant that privilege to ANY process so that the
    /// later UwpLauncher.Launch call (which runs on a background Task
    /// thread, well after our foreground rights would normally have
    /// expired) can still bring the Surface app up properly. Without this
    /// the Surface app activates in a deferred / underprivileged state
    /// and its UIA tree never finishes populating before our search
    /// times out.
    /// </summary>
    private void HotkeyTriggered(Action onUiThread)
    {
        AllowSetForegroundWindow(ASFW_ANY);
        _ui.Post(_ => onUiThread(), null);
    }

    private void ApplyHotkeys()
    {
        _hotkeys.Clear();
        var actionMap = new Dictionary<string, Action>
        {
            { "adaptive",  () => HotkeyTriggered(() => StartSetMode("adaptive"))         },
            { "80",        () => HotkeyTriggered(() => StartSetMode("80"))               },
            { "100-1day",  () => HotkeyTriggered(() => StartSetMode("100", "1day"))      },
            { "100-1week", () => HotkeyTriggered(() => StartSetMode("100", "1week"))     },
            { "cycle",     () => HotkeyTriggered(() => CycleMode())                      },
            { "oneshot",   () => HotkeyTriggered(() => StartTriggerOneShot())            },
            { "power-efficient", () => HotkeyTriggered(() => SetPower(PowerMode.Mode.Efficient))   },
            { "power-balanced",  () => HotkeyTriggered(() => SetPower(PowerMode.Mode.Balanced))    },
            { "power-perf",      () => HotkeyTriggered(() => SetPower(PowerMode.Mode.Performance)) },
            { "schedule-toggle", () => HotkeyTriggered(() => ToggleScheduledFakeSleep())           }
        };

        // Variant-specific filter: skip actions that don't apply to the
        // current variant. Two reasons:
        //   1. UX: a variant B user shouldn't accidentally activate Adaptive
        //      via a hotkey that has no underlying mode to switch to.
        //   2. Collision avoidance: 'oneshot' and 'adaptive' share the
        //      default Ctrl+Shift+1. ApplyHotkeys gracefully picks the
        //      right one based on the detected variant.
        // 'Unknown' variant registers all hotkeys (let the user discover
        // for themselves which work) — same as v1.2.x behavior.
        var skipForA = new HashSet<string> { "oneshot" };
        var skipForB = new HashSet<string> { "adaptive", "80", "100-1day", "100-1week", "cycle" };

        var failures = new List<string>();
        foreach (var (action, h) in _settings.Hotkeys)
        {
            if (!h.Enabled || string.IsNullOrEmpty(h.Key)) continue;
            if (!actionMap.TryGetValue(action, out var cb)) continue;
            if (_currentVariant == SurfaceUiVariant.A && skipForA.Contains(action)) continue;
            if (_currentVariant == SurfaceUiVariant.B && skipForB.Contains(action)) continue;
            var err = _hotkeys.Register(h.Key, cb);
            if (err != null) failures.Add(err);
        }
        if (failures.Count > 0)
            ReportError("Some hotkeys could not be registered:" + Environment.NewLine + "• "
                        + string.Join(Environment.NewLine + "• ", failures));
    }

    private void CycleMode()
    {
        if (_busy) return;
        // Variant B has only one action — "cycling" semantically reduces
        // to "trigger the one-shot." This way users who had the Cycle
        // hotkey bound from v1.2.x continue to get useful behavior after
        // we detect they're on variant B.
        if (_currentVariant == SurfaceUiVariant.B)
        {
            StartTriggerOneShot();
            return;
        }
        var st = StateStore.Load();
        if (st.Mode == "adaptive")           StartSetMode("80");
        else if (st.Mode == "80")            StartSetMode("100", "1day");
        else if (st.Mode == "100" && st.Duration == "1day")  StartSetMode("100", "1week");
        else                                 StartSetMode("adaptive");
    }

    // ---- Cleanup -------------------------------------------------------

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _themeTimer.Stop();
            _themeTimer.Dispose();
            _trimTimer.Stop();
            _trimTimer.Dispose();
            _hotkeys.Dispose();
            // Variant B watcher (null for variant A users — no-op).
            // Unsubscribe StateChanged first for symmetry with the
            // ApplyVariantToMenu transition path; not strictly required
            // since both the watcher and TrayAppContext are dying, but
            // keeps the cleanup pattern consistent.
            if (_oneShotWatcher != null)
            {
                _oneShotWatcher.StateChanged -= OnOneShotStateChanged;
                _oneShotWatcher.Dispose();
                _oneShotWatcher = null;
            }
            // Fake-sleep watchdog event (Phase 9). Static event so we MUST
            // unsubscribe explicitly or we leak this TrayAppContext through
            // the FakeSleepMode static.
            try { FakeSleepMode.WatchdogExited -= OnFakeSleepWatchdogExited; } catch { }
            if (_stateWatcher != null)
            {
                _stateWatcher.EnableRaisingEvents = false;
                _stateWatcher.Dispose();
            }
            // Drop the icon reference before disposing _icon's source bitmaps,
            // so NotifyIcon doesn't hold a dangling pointer.
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _iconWhite.Dispose();
            _iconBlack.Dispose();
            _errorBitmap.Dispose();
        }
        base.Dispose(disposing);
    }
}
