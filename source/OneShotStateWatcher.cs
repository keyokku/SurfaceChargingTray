using System.Windows.Forms;

namespace SurfaceChargingTray;

/// <summary>
/// Variant B only — keeps the tray menu's "Charge to 100%" item enabled-
/// state synchronized with the Surface app's button enabled-state, without
/// polling the Surface app on a timer.
///
/// Architecture: infer state from cheap Win32 power signals (AC/DC,
/// battery percentage). Only open the Surface app to probe when state
/// is genuinely ambiguous — and only at well-defined trigger points.
///
/// Probe triggers:
///   - AC plug-in (battery less than 100%): probe ~60s after, give Smart
///     Charging time to settle on its limit/no-limit decision
///   - Battery hits 80% on AC: wait ~3 min, then probe (only if still
///     at 80% — if battery moved to 81%, Smart Charging isn't limiting
///     and we already know the button is disabled, no probe needed)
///   - Tray menu opening: debounced 30s — captures the "user wants to
///     act now" moment without spamming probes on rapid menu opens
///
/// Direct state inferences (no probe needed):
///   - On DC: button is always disabled (no charging happening)
///   - Battery at 100%: button is always disabled (nothing to charge)
///   - User just successfully invoked the button via our tray (Microsoft
///     greys it after press; we mark Disabled immediately, await next
///     plug-in cycle)
///
/// Variant A: this class is NEVER instantiated. TrayAppContext creates it
/// only when DetectedVariant transitions to B and disposes it on any flip
/// away from B. Variant A users incur zero timers, zero event subscriptions,
/// zero Surface-app probes from this code path.
/// </summary>
internal sealed class OneShotStateWatcher : IDisposable
{
    public enum ButtonState
    {
        /// <summary>Initial state — nothing observed yet. Render as enabled.</summary>
        Unknown,
        /// <summary>Confirmed enabled — Smart Charging limiting, override available.</summary>
        Enabled,
        /// <summary>Confirmed disabled — on DC, at 100%, or override already used this cycle.</summary>
        Disabled
    }

    /// <summary>
    /// Fires on the UI thread whenever the inferred / probed button state
    /// changes. Subscribers should update tray menu .Enabled and .Text.
    /// </summary>
    public event Action<ButtonState>? StateChanged;

    /// <summary>
    /// Probe callback — consumer (TrayAppContext) provides this so the
    /// probe runs serialized against any other Surface-app interaction
    /// (RunSetMode / RunRefresh / RunTriggerOneShot via the _busy flag).
    /// Callback fires the result on the UI thread; null means "couldn't
    /// determine, don't change state."
    /// </summary>
    private readonly Action<Action<bool?>> _probeCallback;

    private readonly SynchronizationContext _ui;
    private readonly System.Windows.Forms.Timer _batteryPoll;
    private readonly System.Windows.Forms.Timer _scheduledProbe;

    // Last-observed power state. Used to detect transitions on each tick.
    private PowerLineStatus _lastPower;
    private int             _lastBatteryPct;
    private bool            _crossedEightyPct;   // true if last poll saw battery >= 80%

    // Probe debouncing.
    private DateTime _lastProbeAt    = DateTime.MinValue;
    private bool     _probeInFlight  = false;

    private ButtonState _lastReported = ButtonState.Unknown;
    private bool        _disposed     = false;

    // Tunables.
    private const int BatteryPollMs           = 60_000;     // 60s background poll
    private const int PostPlugInProbeDelayMs  = 60_000;     // 1 min after plug-in
    private const int Post80ProbeDelayMs      = 180_000;    // 3 min after hitting 80%
    private const int MenuOpenDebounceSec     = 30;         // skip menu-open probe if last probe within this window

    public OneShotStateWatcher(SynchronizationContext ui, Action<Action<bool?>> probeCallback)
    {
        _ui = ui;
        _probeCallback = probeCallback;

        // Capture current power state at construction. Don't fire any
        // events here — the consumer subscribes to StateChanged AFTER
        // construction. Initialize() handles the first report.
        var ps = SystemInformation.PowerStatus;
        _lastPower = ps.PowerLineStatus;
        _lastBatteryPct = SafeBatteryPct(ps);
        _crossedEightyPct = _lastBatteryPct >= 80;

        _batteryPoll = new System.Windows.Forms.Timer { Interval = BatteryPollMs };
        _batteryPoll.Tick += (_, _) => OnBatteryPoll();

        _scheduledProbe = new System.Windows.Forms.Timer();
        _scheduledProbe.Tick += (_, _) =>
        {
            _scheduledProbe.Stop();
            RunProbe();
        };

        // Power-source change event (AC plug/unplug fires StatusChange
        // on a non-UI thread; we marshal back to UI thread for state
        // mutations and timer interactions).
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _batteryPoll.Start();
    }

    /// <summary>
    /// Computes and reports the initial state based on current power
    /// status alone (no probe). Direct inferences only:
    ///   - DC -> Disabled
    ///   - AC + 100% -> Disabled
    ///   - else -> Unknown (consumer renders as enabled until first probe)
    ///
    /// Call this AFTER subscribing to StateChanged, otherwise the first
    /// state event is lost.
    /// </summary>
    public void Initialize()
    {
        if (_lastPower == PowerLineStatus.Offline) Report(ButtonState.Disabled);
        else if (_lastBatteryPct >= 100)           Report(ButtonState.Disabled);
        else                                        Report(ButtonState.Unknown);
    }

    /// <summary>
    /// Called by TrayAppContext when the user opens the tray context menu.
    /// Schedules a probe if it's been more than 30s since the last one,
    /// and only if a probe could plausibly change our state (skip on DC
    /// or at 100% — we already know the answer).
    /// </summary>
    public void OnMenuOpening()
    {
        if (_disposed) return;
        if (_probeInFlight) return;
        if (_lastPower == PowerLineStatus.Offline) return;
        if (_lastBatteryPct >= 100) return;
        var sinceLast = (DateTime.UtcNow - _lastProbeAt).TotalSeconds;
        if (sinceLast < MenuOpenDebounceSec) return;
        ScheduleProbe(0);
    }

    /// <summary>
    /// Called by TrayAppContext after a successful TriggerOneShot. The
    /// Surface app greys its button immediately after invocation, so we
    /// mark Disabled directly without probing.
    /// </summary>
    public void NotifyButtonInvoked()
    {
        if (_disposed) return;
        Report(ButtonState.Disabled);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { _batteryPoll.Stop();   _batteryPoll.Dispose(); }   catch { }
        try { _scheduledProbe.Stop(); _scheduledProbe.Dispose(); } catch { }
    }

    // ---- Internals -----------------------------------------------------

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Mode != Microsoft.Win32.PowerModes.StatusChange) return;
        // SystemEvents handlers can fire on non-UI threads. Marshal back.
        try { _ui.Post(_ => OnBatteryPoll(), null); } catch { }
    }

    private void OnBatteryPoll()
    {
        if (_disposed) return;
        var ps = SystemInformation.PowerStatus;
        var newPower = ps.PowerLineStatus;
        var newPct   = SafeBatteryPct(ps);

        bool plugChanged    = newPower != _lastPower;
        bool justPluggedIn  = plugChanged && newPower == PowerLineStatus.Online;
        bool justUnplugged  = plugChanged && newPower == PowerLineStatus.Offline;
        bool wasBelow80     = !_crossedEightyPct;
        bool reachedEighty  = wasBelow80 && newPct >= 80;
        bool reached100     = _lastBatteryPct < 100 && newPct >= 100;

        // Update remembered state for next tick.
        _crossedEightyPct = newPct >= 80;
        _lastPower = newPower;
        _lastBatteryPct = newPct;

        // ---- Direct inferences (no probe needed) ----
        if (justUnplugged || newPower == PowerLineStatus.Offline)
        {
            Report(ButtonState.Disabled);
            return;
        }
        if (reached100 || newPct >= 100)
        {
            Report(ButtonState.Disabled);
            return;
        }

        // ---- Probe-deserving transitions ----
        if (justPluggedIn)
        {
            // Just plugged in (battery < 100%, since we returned above).
            // Wait 60s for Smart Charging to settle on its decision, then probe.
            ScheduleProbe(PostPlugInProbeDelayMs);
            return;
        }
        if (reachedEighty)
        {
            // Just hit 80%. Wait 3 min; if battery is still at 80% then,
            // Smart Charging is engaged and the button is enabled. If
            // battery moved to 81%+, we'll catch that on the next tick
            // and mark accordingly.
            ScheduleProbe(Post80ProbeDelayMs);
            return;
        }

        // No interesting transition this tick. Keep last reported state.
    }

    private void ScheduleProbe(int delayMs)
    {
        if (_disposed) return;
        if (_probeInFlight) return;
        _scheduledProbe.Stop();
        if (delayMs <= 0)
        {
            RunProbe();
        }
        else
        {
            _scheduledProbe.Interval = delayMs;
            _scheduledProbe.Start();
        }
    }

    private void RunProbe()
    {
        if (_disposed) return;
        if (_probeInFlight) return;

        // Re-check direct inferences before paying the probe cost. State
        // may have changed since the probe was scheduled (e.g. user
        // unplugged during the 3-minute wait after hitting 80%).
        var ps = SystemInformation.PowerStatus;
        if (ps.PowerLineStatus == PowerLineStatus.Offline)
        {
            Report(ButtonState.Disabled);
            return;
        }
        var pct = SafeBatteryPct(ps);
        if (pct >= 100)
        {
            Report(ButtonState.Disabled);
            return;
        }

        _probeInFlight = true;
        try
        {
            _probeCallback(result =>
            {
                if (_disposed) return;
                _probeInFlight = false;
                _lastProbeAt = DateTime.UtcNow;
                if (result.HasValue)
                    Report(result.Value ? ButtonState.Enabled : ButtonState.Disabled);
                // null result: probe failed (Surface app issue, busy, etc.).
                // Don't mutate state — keep whatever we last reported.
            });
        }
        catch (Exception ex)
        {
            _probeInFlight = false;
            Logger.Error($"[ERR ] OneShotStateWatcher probe dispatch: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Report(ButtonState s)
    {
        if (s == _lastReported) return;
        _lastReported = s;
        try { StateChanged?.Invoke(s); } catch { }
    }

    /// <summary>
    /// SystemInformation.PowerStatus.BatteryLifePercent returns 1.0f when
    /// the battery info is unavailable (NoSystemBattery / Unknown). Convert
    /// to percent and clamp to a sensible range.
    /// </summary>
    private static int SafeBatteryPct(PowerStatus ps)
    {
        try
        {
            var f = ps.BatteryLifePercent;
            if (float.IsNaN(f) || f < 0) return 0;
            if (f > 1f) return 100;   // already a percent or sentinel
            return (int)Math.Round(f * 100);
        }
        catch { return 0; }
    }
}
