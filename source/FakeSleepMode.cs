using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace SurfaceChargingTray;

/// <summary>
/// Abstract action that simulated-sleep's scheduled-fire timer can invoke
/// when the deferred wake time arrives. The two concrete subtypes map 1:1
/// to the two Surface app UI variants we support:
///
///   SetModeAction = variant A — flips one of the three charging-mode
///                   radios. The v1.2.x scheduler exclusively used this;
///                   variant A users keep this exact behavior in v1.3.0.
///
///   TriggerOneShotAction = variant B — invokes the single 'Charge to
///                          100%' override button. Added in v1.3.0 to
///                          give variant B users an equivalent scheduler.
///
/// Adding a new variant later is a matter of dropping in another subtype;
/// FakeSleepMode itself stays variant-agnostic.
/// </summary>
internal abstract class ScheduledAction
{
    /// <summary>Run the action. Returns null on success, error string on failure.</summary>
    public abstract string? Execute();

    /// <summary>One-line human-readable description for log lines. Not localized.</summary>
    public abstract string Describe();
}

internal sealed class SetModeAction : ScheduledAction
{
    public string  Mode     { get; set; } = "";
    public string? Duration { get; set; }
    public override string? Execute() => SurfaceController.SetMode(Mode, Duration);
    public override string Describe() =>
        Duration != null ? $"SetMode({Mode}, {Duration})" : $"SetMode({Mode})";
}

internal sealed class TriggerOneShotAction : ScheduledAction
{
    public override string? Execute() => SurfaceController.TriggerOneShot();
    public override string Describe() => "TriggerOneShot";
}

/// <summary>
/// One scheduled fire: an action plus how many seconds after fake-sleep
/// entry it should run. v1.4.0 multi-slot scheduling passes a list of
/// these to <see cref="FakeSleepMode.Enter(System.Collections.Generic.List{ScheduledFire}, SettingsModel.ScheduleExitMode)"/>.
/// </summary>
internal sealed class ScheduledFire
{
    public ScheduledAction Action       { get; set; } = null!;
    public int             DelaySeconds { get; set; }
}

/// <summary>
/// "Simulated sleep" mode — the engine behind the v1.2.0 charging-mode
/// scheduler. Class name keeps the original "FakeSleep" internally; the
/// user-visible wording is "simulated sleep".
///
/// Why this exists:
///   Locked-screen, screen-off, and true-sleep scheduled fires all fail —
///   Windows defers UWP rendering when the screen is off, so UI Automation
///   can't drive the Surface app's UI tree. The workaround is to keep the
///   device fully active behind a fullscreen black overlay so UWP keeps
///   painting, while the user perceives "device is asleep".
///
/// Enter():
///   1. Plugged-in-only guard (refuses on battery — would drain).
///   2. Snapshot originals (brightness, Power mode); persist to a recovery
///      JSON so a mid-run crash can restore them on next launch.
///   3. Apply: brightness -> 0, Power mode -> Efficient,
///      SetThreadExecutionState(SYSTEM | DISPLAY) to override the user's
///      Sleep / Screen-off timeouts WITHOUT editing the power scheme.
///   4. Show fullscreen topmost black overlay on every monitor.
///   5. If a scheduled mode + delay was supplied, arm a one-shot timer.
///
/// Exit():
///   Triggered by mouse click / keystroke on any overlay, by a second press
///   of the schedule-toggle hotkey, or automatically after a scheduled fire
///   if auto-exit is enabled. Restores everything in reverse order.
/// </summary>
internal static class FakeSleepMode
{
    // ---- Win32 ---------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS        = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED   = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED  = 0x00000002;

    // ---- State ---------------------------------------------------------

    private static bool _active;
    private static byte? _origBrightness;
    private static PowerMode.Mode? _origPowerMode;
    private static readonly List<Form> _overlays = new();
    // Multi-slot scheduled fires (v1.4.0). The engine sorts fires by delay,
    // arms a single timer for the next-due fire, and re-arms after each one
    // runs. _exitMode governs teardown (Stay / AfterFirst / AfterAll).
    // v1.3.0 had a single ScheduledAction + bool auto-exit; this generalizes
    // both. Empty _fires means "enter fake-sleep with no scheduled action"
    // (manual dismiss only).
    private static System.Windows.Forms.Timer? _scheduledFire;
    private static List<ScheduledFire> _fires = new();
    private static int _fireIndex;
    private static DateTime _fakeSleepEnteredAt;
    private static SettingsModel.ScheduleExitMode _exitMode = SettingsModel.ScheduleExitMode.Stay;
    // Suppress the overlay's "reassert topmost on Deactivate" handler while
    // a scheduled SetMode is in flight. Otherwise the handler will pull
    // focus back from the Surface app mid-UIA-traversal — the same race
    // that broke locked-screen attempts (UWP stops painting when foreground
    // ownership changes mid-paint).
    private static bool _suppressTopmostReassert;

    public static bool IsActive => _active;

    // ---- Safety watchdog (Phase 9) -------------------------------------
    //
    // After the 2026-05-16 incident — where USB-C PD silently failed mid-night,
    // the system kept consuming ~10-13 W from battery instead of charging,
    // and our fake-sleep held the device active until physical battery death
    // — we added these guards. They're not preventative (we can't fix
    // Windows/Surface PD failures from user-mode) but they CONTAIN the
    // damage: any sustained "supposedly charging but battery dropping"
    // condition force-exits fake-sleep within a few minutes, letting Windows
    // enter real Modern Standby (which often resets the stuck PD state).
    //
    // All guards share a single 60s WinForms.Timer. Each tick does 2 cheap
    // Win32 reads + 4 numeric compares + 1 log line — negligible power.
    private static System.Windows.Forms.Timer? _watchdogTimer;
    private static DateTime _watchdogEnteredAt;
    private static int      _watchdogBatteryAtEnter;
    private static readonly List<(DateTime At, int Pct)> _watchdogSamples = new();
    private static bool _watchdogTriggered;   // prevents recursive exit during tick

    // Tunables. Conservative defaults; user-tunable in a future settings UI.
    private const int    WatchdogTickMs              = 60_000;   // 60s polling
    private const int    WatchdogMaxFakeSleepHours   = 23;       // hard duration cap (overnight schedules
                                                                  // need 8-12h of headroom; 23 is "definitely
                                                                  // a runaway by now, no legitimate use case")
    private const int    WatchdogLowBatteryFloorPct  = 30;       // exit below this %
    // Rate-based AC-health detection: only kicks in BELOW this battery level,
    // because Smart-Charging-set-to-80% will legitimately discharge from
    // 100% down to 80% while plugged in (firmware deliberately holds the cap).
    // Above 80% we don't know if drops are Smart-Charging-discharge or PD
    // failure; below 80% with charging set, drops are anomalous.
    private const int    WatchdogSmartChargeCapPct   = 80;
    private const int    WatchdogDropThresholdPct    = 3;        // % drop that flags as anomalous
    private const int    WatchdogDropWindowMinutes   = 10;       // ...over this rolling window
    private const int    WatchdogMinConsistentSamples = 3;       // ...require at least N samples with
                                                                  // no upward bounce >1% (filters reading noise
                                                                  // and brief workload spikes)

    /// <summary>
    /// Fires on the UI thread when a watchdog force-exits fake-sleep. The
    /// string is a short human-readable reason suitable for a balloon
    /// notification (TrayAppContext subscribes to surface this to the user).
    /// </summary>
    public static event Action<string>? WatchdogExited;

    // ---- Enter ---------------------------------------------------------

    /// <summary>
    /// Enter fake-sleep with a list of scheduled fires (v1.4.0 multi-slot).
    /// Each fire runs its action <see cref="ScheduledFire.DelaySeconds"/>
    /// after entry, while fake-sleep holds the device awake so UWP renders
    /// and UIA can drive the Surface app. Fires run in delay order.
    ///
    /// <paramref name="exitMode"/> governs teardown:
    ///   Stay       — never auto-exit (user dismisses manually)
    ///   AfterFirst — exit right after the first fire runs
    ///   AfterAll   — exit after the last (latest) fire runs
    ///
    /// An empty <paramref name="fires"/> list just enters fake-sleep with no
    /// scheduled action (manual-dismiss). Returns null on success, or a
    /// short error string if entry was refused (e.g. on battery).
    /// </summary>
    public static string? Enter(
        List<ScheduledFire> fires,
        SettingsModel.ScheduleExitMode exitMode = SettingsModel.ScheduleExitMode.Stay)
    {
        if (_active) return null;

        // Plugged-in-only guard: simulated sleep is exclusively for
        // overnight charging-mode flips while on AC. On battery, keeping
        // the device fully active under a black overlay would silently
        // drain the battery — exactly the opposite of what the user wants.
        var power = SystemInformation.PowerStatus;
        if (power.PowerLineStatus == PowerLineStatus.Offline)
            return "Simulated sleep requires the device to be plugged in.";

        _active = true;
        // Sort fires by delay so we arm them in chronological order.
        _fires = (fires ?? new List<ScheduledFire>())
            .Where(f => f.Action != null && f.DelaySeconds > 0)
            .OrderBy(f => f.DelaySeconds)
            .ToList();
        _fireIndex = 0;
        _exitMode = exitMode;
        _fakeSleepEnteredAt = DateTime.UtcNow;

        // --- 1. Snapshot originals (do this BEFORE any mutation) --------
        try { _origBrightness = ReadBrightness(); }
        catch (Exception ex)
        {
            _origBrightness = null;
            Logger.Error($"[ERR ] FakeSleep: SAVE brightness: {ex.GetType().Name}: {ex.Message}");
        }
        try { _origPowerMode = PowerMode.Get(); }
        catch (Exception ex)
        {
            _origPowerMode = null;
            Logger.Error($"[ERR ] FakeSleep: SAVE Power mode: {ex.GetType().Name}: {ex.Message}");
        }

        // Persist originals to disk BEFORE mutating so a mid-run crash
        // can be recovered on next startup. See RestoreOnStartup().
        WriteRecoveryState();

        // --- 2. Apply low-power settings --------------------------------
        try { SetBrightness(0); }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: SET brightness=0: {ex.GetType().Name}: {ex.Message}");
        }
        try
        {
            if (!PowerMode.Set(PowerMode.Mode.Efficient))
                Logger.Error("[ERR ] FakeSleep: SET Power mode=Efficient returned false");
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: SET Power mode: {ex.GetType().Name}: {ex.Message}");
        }

        // Overrides "Sleep=Never" + "Screen-off=Never" for the duration of
        // simulated sleep without editing the user's power scheme — auto-
        // reverts on ES_CONTINUOUS release / process death.
        try
        {
            if (SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED) == 0)
                Logger.Error("[ERR ] FakeSleep: SetThreadExecutionState returned 0 (FAILED)");
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: SetThreadExecutionState: {ex.GetType().Name}: {ex.Message}");
        }

        // --- 3. Show the black overlay on every monitor -----------------
        try { ShowOverlays(); }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: ShowOverlays: {ex.GetType().Name}: {ex.Message}");
            Exit();
            return "Could not show the simulated-sleep overlay: " + ex.Message;
        }

        // --- 3b. Start safety watchdog (Phase 9) ------------------------
        // Single 60s timer that runs all 4 guards (AC-health, AC-disconnect,
        // low-battery-floor, hard-duration-cap). See class comment block above.
        StartWatchdog();

        // --- 4. Arm the first scheduled fire, if any -------------------
        if (_fires.Count > 0)
        {
            ArmNextFire();
            var modes = string.Join(", ", _fires.Select(f => $"{f.Action.Describe()}@{f.DelaySeconds}s"));
            Logger.Error($"[INFO] Scheduled {_fires.Count} fire(s): {modes} exit={_exitMode}");
        }

        return null;
    }

    /// <summary>
    /// Arms a single one-shot timer for the next fire in the queue (relative
    /// to entry time so cumulative drift doesn't accrue). No-op when the
    /// queue is exhausted.
    /// </summary>
    private static void ArmNextFire()
    {
        if (_fireIndex >= _fires.Count) return;
        try
        {
            var next = _fires[_fireIndex];
            var elapsed = (DateTime.UtcNow - _fakeSleepEnteredAt).TotalSeconds;
            // Remaining time until this fire's absolute delay. At least 1 ms
            // (fire essentially now if we're already past it). Clamp to Int32.
            double remainingMs = Math.Max(1.0, (next.DelaySeconds - elapsed) * 1000.0);
            int intervalMs = (int)Math.Min(remainingMs, int.MaxValue);

            _scheduledFire?.Stop();
            _scheduledFire?.Dispose();
            _scheduledFire = new System.Windows.Forms.Timer { Interval = intervalMs };
            _scheduledFire.Tick += OnScheduledFire;
            _scheduledFire.Start();
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: ArmNextFire: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void OnScheduledFire(object? sender, EventArgs e)
    {
        // One-shot: stop immediately so we don't refire if the action takes
        // longer than the interval (which it will — SetMode/TriggerOneShot
        // can be 5-30s on a cold Surface app).
        try { _scheduledFire?.Stop(); } catch { }

        if (_fireIndex >= _fires.Count) return;
        var fire = _fires[_fireIndex];
        bool isLastFire = _fireIndex == _fires.Count - 1;
        _fireIndex++;   // advance now so re-arming targets the next slot

        // Suppress the overlay's Deactivate->reassert-topmost handler for
        // the duration of the UIA traversal. Yanking focus back from the
        // Surface app mid-paint is the failure mode we hit on locked-screen.
        _suppressTopmostReassert = true;

        // Run on a thread-pool thread so the UI thread stays free (the
        // overlay needs to keep painting; UIA can take 5-30s).
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // Device is active, screen "off" only visually (brightness 0
                // + black overlay). UWP renders normally. Default
                // SkipWindowHide=false so HideWindow pushes the Surface app
                // back behind our overlay after UIA is done.
                SurfaceController.SkipWindowHide = false;

                var err = fire.Action.Execute();
                if (err != null)
                    Logger.Error($"[ERR ] Scheduled {fire.Action.Describe()} FAILED: {err}");
                else
                    Logger.Error($"[INFO] Scheduled {fire.Action.Describe()} OK");
            }
            catch (Exception ex)
            {
                Logger.Error($"[ERR ] Scheduled {fire.Action.Describe()} CRASHED: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _suppressTopmostReassert = false;

                // Decide whether to exit, arm the next fire, or just hold.
                bool shouldExit = _exitMode switch
                {
                    SettingsModel.ScheduleExitMode.AfterFirst => true,
                    SettingsModel.ScheduleExitMode.AfterAll   => isLastFire,
                    _                                          => false  // Stay
                };

                try
                {
                    var first = _overlays.FirstOrDefault();
                    if (first != null && !first.IsDisposed)
                    {
                        first.BeginInvoke(() =>
                        {
                            try
                            {
                                if (shouldExit)
                                {
                                    // Auto-exit lets the device's real Sleep /
                                    // Screen-off timeouts take over.
                                    Exit();
                                }
                                else
                                {
                                    // Re-grab keyboard focus so dismiss-on-key
                                    // still works, then arm the next fire (if any).
                                    try { first.Activate(); } catch { }
                                    ArmNextFire();
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[ERR ] FakeSleep: post-fire dispatch: {ex.GetType().Name}: {ex.Message}");
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[ERR ] FakeSleep: post-fire BeginInvoke: {ex.GetType().Name}: {ex.Message}");
                }
            }
        });
    }

    // ---- Exit ----------------------------------------------------------

    public static void Exit()
    {
        if (!_active) return;

        // Stop the safety watchdog first so its ticks can't race the exit
        // teardown. Idempotent — no-op if watchdog wasn't started.
        StopWatchdog();

        // Cancel any pending scheduled fire + clear the fire queue.
        try
        {
            _scheduledFire?.Stop();
            _scheduledFire?.Dispose();
            _scheduledFire = null;
            _fires = new List<ScheduledFire>();
            _fireIndex = 0;
            _suppressTopmostReassert = false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: scheduled fire cleanup: {ex.GetType().Name}: {ex.Message}");
        }

        // 1. Tear down overlays first so the user sees something happen.
        try { HideOverlays(); }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: HideOverlays: {ex.GetType().Name}: {ex.Message}");
        }

        // 2. Release sleep/display lock — restores the user's actual
        // Power-options timeouts.
        try { SetThreadExecutionState(ES_CONTINUOUS); }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: SetThreadExecutionState release: {ex.GetType().Name}: {ex.Message}");
        }

        // 3. Restore Power mode.
        if (_origPowerMode.HasValue && _origPowerMode.Value != PowerMode.Mode.Unknown)
        {
            try
            {
                if (!PowerMode.Set(_origPowerMode.Value))
                    Logger.Error($"[ERR ] FakeSleep: RESTORE Power mode={_origPowerMode.Value} returned false");
            }
            catch (Exception ex)
            {
                Logger.Error($"[ERR ] FakeSleep: RESTORE Power mode: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 4. Restore brightness.
        if (_origBrightness.HasValue)
        {
            try { SetBrightness(_origBrightness.Value); }
            catch (Exception ex)
            {
                Logger.Error($"[ERR ] FakeSleep: RESTORE brightness: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Clear recovery state file: a successful Exit() means there is
        // nothing for the next startup to recover.
        ClearRecoveryState();

        _origBrightness = null;
        _origPowerMode  = null;
        _exitMode = SettingsModel.ScheduleExitMode.Stay;
        _active = false;
    }

    // ---- Safety watchdog implementation (Phase 9) ----------------------

    private static void StartWatchdog()
    {
        try
        {
            StopWatchdog();   // defensive — never run two timers at once
            _watchdogEnteredAt = DateTime.UtcNow;
            _watchdogBatteryAtEnter = ReadBatteryPct();
            _watchdogSamples.Clear();
            _watchdogSamples.Add((_watchdogEnteredAt, _watchdogBatteryAtEnter));
            _watchdogTriggered = false;

            _watchdogTimer = new System.Windows.Forms.Timer { Interval = WatchdogTickMs };
            _watchdogTimer.Tick += (_, _) => OnWatchdogTick();
            _watchdogTimer.Start();
            Logger.Error($"[INFO] FakeSleep watchdog armed (battery at enter: {_watchdogBatteryAtEnter}%)");
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: StartWatchdog: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void StopWatchdog()
    {
        try
        {
            _watchdogTimer?.Stop();
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
            _watchdogSamples.Clear();
        }
        catch { }
    }

    private static void OnWatchdogTick()
    {
        if (!_active || _watchdogTriggered) return;

        try
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            var nowUtc = DateTime.UtcNow;
            var pct = ReadBatteryPct();
            var pluggedIn = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

            _watchdogSamples.Add((nowUtc, pct));
            // Prune samples older than the drop window — list stays bounded at
            // ~11 entries (10 min window / 60s tick + 1).
            var cutoff = nowUtc.AddMinutes(-WatchdogDropWindowMinutes);
            _watchdogSamples.RemoveAll(s => s.At < cutoff);

            Logger.Error($"[INFO] Watchdog tick: battery={pct}% pluggedIn={pluggedIn} elapsedMin={Math.Round((nowUtc - _watchdogEnteredAt).TotalMinutes,1)}");

            // ---- Guard 1: hard duration cap -----------------------------
            if ((nowUtc - _watchdogEnteredAt).TotalHours >= WatchdogMaxFakeSleepHours)
            {
                TriggerWatchdogExit($"Simulated sleep ran for {WatchdogMaxFakeSleepHours} hours — exiting as a safety cap.");
                return;
            }

            // ---- Guard 2: AC unplugged ----------------------------------
            // Enter() refuses to start on battery, so an Offline reading mid-run
            // means cable was pulled. Exit immediately so the device can use real
            // Modern Standby instead of being held active on battery.
            if (!pluggedIn)
            {
                TriggerWatchdogExit("Power cable disconnected during simulated sleep — exiting to allow normal sleep.");
                return;
            }

            // ---- Guard 3: low-battery floor -----------------------------
            // Even with charging supposedly working, never let fake-sleep run
            // the battery below this floor. Prevents the death-spiral end
            // state where the system has too little headroom to recover.
            if (pct <= WatchdogLowBatteryFloorPct)
            {
                TriggerWatchdogExit($"Battery hit {pct}% during simulated sleep — exiting to protect the battery (floor is {WatchdogLowBatteryFloorPct}%).");
                return;
            }

            // ---- Guard 4: AC health (the critical one) ------------------
            // The 2026-05-16 incident's signature: PowerLineStatus.Online was
            // true, but charging silently delivered 0 W. Battery quietly drained
            // ~10 W for hours. Detect: sustained downward % movement while
            // 'plugged in'.
            //
            // Only active BELOW the Smart-Charging cap (80%). Above 80% the
            // device may be legitimately discharging down to its set cap
            // (e.g., user kept it at 100% temporarily and Smart Charging is
            // now letting it return to the 80% hold level) — we don't want
            // to false-flag normal firmware behavior.
            //
            // Below 80%: any sustained downward trend while plugged in is
            // anomalous. Require N consistent samples (no upward bounce >1%)
            // to filter out reading noise and brief workload spikes that
            // briefly exceed charger output (e.g., background video render
            // CPU bursts).
            if (pct < WatchdogSmartChargeCapPct)
            {
                var windowStart = nowUtc.AddMinutes(-WatchdogDropWindowMinutes);
                var samplesInWindow = _watchdogSamples.Where(s => s.At >= windowStart).OrderBy(s => s.At).ToList();
                if (samplesInWindow.Count >= WatchdogMinConsistentSamples)
                {
                    var dropPct = samplesInWindow[0].Pct - pct;
                    // Check for consistent decline: no sample may sit >1% above
                    // its predecessor (allow flat or downward; small upward
                    // wobble within reading noise is OK).
                    bool consistentDecline = true;
                    for (int i = 1; i < samplesInWindow.Count; i++)
                    {
                        if (samplesInWindow[i].Pct > samplesInWindow[i-1].Pct + 1)
                        {
                            consistentDecline = false;
                            break;
                        }
                    }
                    if (dropPct >= WatchdogDropThresholdPct && consistentDecline)
                    {
                        TriggerWatchdogExit(
                            $"Battery dropped {dropPct}% in the last {WatchdogDropWindowMinutes} min despite being plugged in — " +
                            $"charging appears to have stopped delivering power. Exiting to let Windows reset USB-C power negotiation.");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Watchdog must NEVER make things worse. Swallow errors and keep ticking.
            Logger.Error($"[ERR ] FakeSleep watchdog tick: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TriggerWatchdogExit(string reason)
    {
        if (_watchdogTriggered) return;
        _watchdogTriggered = true;
        Logger.Error($"[INFO] WATCHDOG EXIT: {reason}");

        // Dispatch to UI thread for the actual Exit() + event fire. Watchdog
        // tick already runs on UI thread (WinForms.Timer.Tick is UI-thread),
        // but be defensive in case this ever gets called from elsewhere.
        try
        {
            var first = _overlays.FirstOrDefault();
            if (first != null && !first.IsDisposed)
            {
                first.BeginInvoke(() =>
                {
                    try { Exit(); }            catch { }
                    try { WatchdogExited?.Invoke(reason); } catch { }
                });
            }
            else
            {
                try { Exit(); }            catch { }
                try { WatchdogExited?.Invoke(reason); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: TriggerWatchdogExit dispatch: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads current battery % (0-100) using the same logic as the broader app.
    /// Returns 0 if reading fails — that triggers the low-battery guard, which
    /// is the safest behavior when we can't tell what's happening.
    /// </summary>
    private static int ReadBatteryPct()
    {
        try
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            var f = ps.BatteryLifePercent;
            if (float.IsNaN(f) || f < 0) return 0;
            if (f > 1f) return 100;
            return (int)Math.Round(f * 100);
        }
        catch { return 0; }
    }

    // ---- Crash recovery ------------------------------------------------
    //
    // Fake-sleep mutates two real-world settings (brightness, Windows Power
    // mode). The exec-state lock auto-clears on process death, but brightness
    // and Power mode don't — a crash mid-fake-sleep would leave the user with
    // a black screen and Best-Efficiency stuck until they manually fixed it.
    //
    // To recover: write the originals to a small JSON file the moment we have
    // them captured (BEFORE applying low-power). If we Exit() cleanly we
    // delete the file. If the process dies mid-fake-sleep, the file lingers,
    // and RestoreOnStartup() at the next launch reads it, restores the
    // originals, and deletes the file.

    private static string RecoveryPath =>
        Path.Combine(Paths.DataDir, "fakesleep-recovery.json");

    private static void WriteRecoveryState()
    {
        try
        {
            var rec = new
            {
                Brightness = _origBrightness,
                PowerMode  = _origPowerMode?.ToString(),
                Timestamp  = DateTime.Now.ToString("s")
            };
            File.WriteAllText(RecoveryPath, JsonSerializer.Serialize(rec));
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: WriteRecoveryState: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ClearRecoveryState()
    {
        try
        {
            if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath);
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: ClearRecoveryState: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Called once at program startup. If a recovery state file is present
    /// from a previous crash mid-fake-sleep, restore the user's brightness
    /// and Power mode, then delete the file. Silent no-op if no file.
    /// </summary>
    public static void RestoreOnStartup()
    {
        try
        {
            if (!File.Exists(RecoveryPath)) return;
            Logger.Error("[INFO] Recovering brightness/Power mode from previous run");

            byte? bright = null;
            PowerMode.Mode? pm = null;
            try
            {
                var json = File.ReadAllText(RecoveryPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Brightness", out var b) && b.ValueKind == JsonValueKind.Number)
                    bright = (byte)b.GetInt32();
                if (doc.RootElement.TryGetProperty("PowerMode", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    if (Enum.TryParse<PowerMode.Mode>(p.GetString(), out var m)) pm = m;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[ERR ] FakeSleep: recovery JSON parse: {ex.GetType().Name}: {ex.Message}");
            }

            if (bright.HasValue)
            {
                try { SetBrightness(bright.Value); }
                catch (Exception ex)
                {
                    Logger.Error($"[ERR ] FakeSleep: recovery SET brightness: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (pm.HasValue && pm.Value != PowerMode.Mode.Unknown)
            {
                try { PowerMode.Set(pm.Value); }
                catch (Exception ex)
                {
                    Logger.Error($"[ERR ] FakeSleep: recovery SET Power mode: {ex.GetType().Name}: {ex.Message}");
                }
            }

            try { File.Delete(RecoveryPath); } catch { }
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: RestoreOnStartup: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- Brightness via WMI -------------------------------------------

    /// <summary>Read CurrentBrightness (0-100). Throws on systems where
    /// WmiMonitorBrightness isn't exposed — most external monitors don't
    /// support it; built-in Surface displays do.</summary>
    private static byte ReadBrightness()
    {
        using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness");
        using var col = s.Get();
        foreach (ManagementObject mo in col)
        {
            using (mo)
            {
                object val = mo["CurrentBrightness"];
                return Convert.ToByte(val);
            }
        }
        throw new InvalidOperationException("WmiMonitorBrightness returned no instances");
    }

    /// <summary>Set brightness. timeout=1 means "apply immediately and
    /// return when done". WMI may quantize to the nearest supported level.
    ///
    /// The display driver advertises a discrete set of supported brightness
    /// values via WmiMonitorBrightness.Levels — not guaranteed to include
    /// 0 or to be a contiguous 0-100 range. We pass the caller's requested
    /// percent through SnapToValidLevel() so a request like "0" maps to the
    /// device's true minimum supported level when 0 itself isn't in the set.
    /// </summary>
    private static void SetBrightness(byte percent)
    {
        byte target = SnapToValidLevel(percent);
        using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
        using var col = s.Get();
        foreach (ManagementObject mo in col)
        {
            using (mo)
            {
                mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, target });
            }
        }
    }

    /// <summary>Find the closest brightness level in the driver's
    /// advertised Levels array. Returns the input unchanged if the Levels
    /// query fails (preserving prior behavior on devices that don't expose
    /// it cleanly).</summary>
    private static byte SnapToValidLevel(byte requested)
    {
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT Levels FROM WmiMonitorBrightness");
            using var col = s.Get();
            foreach (ManagementObject mo in col)
            {
                using (mo)
                {
                    if (mo["Levels"] is not byte[] levels || levels.Length == 0)
                        continue;
                    byte best = levels[0];
                    int bestDist = Math.Abs(levels[0] - requested);
                    for (int i = 1; i < levels.Length; i++)
                    {
                        int d = Math.Abs(levels[i] - requested);
                        if (d < bestDist) { bestDist = d; best = levels[i]; }
                    }
                    return best;
                }
            }
        }
        catch { }
        return requested;
    }

    // ---- Overlay forms -------------------------------------------------

    private static void ShowOverlays()
    {
        _overlays.Clear();
        foreach (Screen scr in Screen.AllScreens)
        {
            var f = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                BackColor       = Color.Black,
                TopMost         = true,
                ShowInTaskbar   = false,
                StartPosition   = FormStartPosition.Manual,
                Bounds          = scr.Bounds,
                KeyPreview      = true,  // so KeyDown fires regardless of focused child
            };
            // Dismiss on click OR key. Mouse-MOVE intentionally NOT
            // wired — user explicitly asked for this so pen/cat drift
            // doesn't break the "fake sleep" overnight.
            f.MouseDown    += (_, _) => ExitFromUiThread();
            f.MouseClick   += (_, _) => ExitFromUiThread();
            f.KeyDown      += (_, _) => ExitFromUiThread();
            // Reassert topmost if some app steals focus (notifications,
            // background launches). Do NOT auto-dismiss on deactivate —
            // the whole point of fake-sleep is unattended persistence.
            //
            // The _suppressTopmostReassert gate is critical: while a
            // scheduled SetMode is running we WANT the Surface app to
            // hold foreground long enough for UWP to paint its UI tree.
            // Yanking focus back here would re-trigger the same UWP-defer
            // behavior that blocks locked-screen scheduled fires.
            f.Deactivate += (_, _) =>
            {
                if (_suppressTopmostReassert) return;
                try
                {
                    f.BeginInvoke(() =>
                    {
                        try
                        {
                            f.TopMost = false;
                            f.TopMost = true;
                            f.Activate();
                        }
                        catch { }
                    });
                }
                catch { }
            };
            f.Load       += (_, _) => { try { Cursor.Hide(); } catch { } };
            f.FormClosed += (_, _) => { try { Cursor.Show(); } catch { } };
            f.Show();
            _overlays.Add(f);
        }
        if (_overlays.Count > 0)
        {
            try { _overlays[0].Activate(); } catch { }
        }
    }

    private static void HideOverlays()
    {
        try { Cursor.Show(); } catch { }
        foreach (var f in _overlays)
        {
            try { f.Close();   } catch { }
            try { f.Dispose(); } catch { }
        }
        _overlays.Clear();
    }

    private static void ExitFromUiThread()
    {
        try { Exit(); }
        catch (Exception ex)
        {
            Logger.Error($"[ERR ] FakeSleep: ExitFromUiThread FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
