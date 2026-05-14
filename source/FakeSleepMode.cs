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
    private static bool _autoExitAfterFire;
    private static readonly List<Form> _overlays = new();
    // Scheduled action to run while simulated sleep is active. One-shot timer.
    // Generalized in v1.3.0 from {mode, duration} pair to a polymorphic
    // ScheduledAction so variant B's TriggerOneShot can ride the same engine
    // as variant A's SetMode. Null when no fire is scheduled.
    private static System.Windows.Forms.Timer? _scheduledFire;
    private static ScheduledAction? _scheduledAction;
    // Suppress the overlay's "reassert topmost on Deactivate" handler while
    // a scheduled SetMode is in flight. Otherwise the handler will pull
    // focus back from the Surface app mid-UIA-traversal — the same race
    // that broke locked-screen attempts (UWP stops painting when foreground
    // ownership changes mid-paint).
    private static bool _suppressTopmostReassert;

    public static bool IsActive => _active;

    // ---- Enter ---------------------------------------------------------

    /// <summary>
    /// Variant A back-compat shim. Existing callers (TrayAppContext schedule-
    /// toggle) pass a charging mode + optional duration; we wrap those into a
    /// <see cref="SetModeAction"/> and delegate to the canonical action-based
    /// overload below. Behavior identical to v1.2.x.
    /// </summary>
    public static string? Enter(
        string? scheduledMode = null,
        string? scheduledDuration = null,
        int delaySeconds = 0,
        bool autoExitAfterFire = false)
    {
        ScheduledAction? action = null;
        if (!string.IsNullOrEmpty(scheduledMode))
            action = new SetModeAction { Mode = scheduledMode, Duration = scheduledDuration };
        return Enter(action, delaySeconds, autoExitAfterFire);
    }

    /// <summary>
    /// Enter fake-sleep. If <paramref name="action"/> is non-null, a one-shot
    /// timer fires <paramref name="delaySeconds"/> later and runs the action
    /// in the background — while fake-sleep is still holding the device
    /// awake, so UWP renders normally and UIA can drive the Surface app's
    /// UI tree.
    ///
    /// Action types: <see cref="SetModeAction"/> for variant A (radio flip)
    /// or <see cref="TriggerOneShotAction"/> for variant B (button invoke).
    ///
    /// Returns null on success, or a short error string if entry was refused
    /// (e.g. on battery). Caller is responsible for surfacing the message.
    /// </summary>
    public static string? Enter(
        ScheduledAction? action,
        int delaySeconds = 0,
        bool autoExitAfterFire = false)
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
        _autoExitAfterFire = autoExitAfterFire;

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

        // --- 4. Schedule the action fire, if requested -----------------
        if (action != null && delaySeconds > 0)
        {
            try
            {
                _scheduledAction = action;
                // System.Windows.Forms.Timer interval is Int32 ms — max
                // ~24.85 days. Clamp defensively.
                long ms = Math.Min((long)delaySeconds * 1000L, int.MaxValue);
                _scheduledFire = new System.Windows.Forms.Timer { Interval = (int)ms };
                _scheduledFire.Tick += OnScheduledFire;
                _scheduledFire.Start();
                var autoEx = autoExitAfterFire ? " auto-exit" : "";
                Logger.Error($"[INFO] Scheduled {action.Describe()} in {delaySeconds}s{autoEx}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[ERR ] FakeSleep: schedule timer start: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return null;
    }

    private static void OnScheduledFire(object? sender, EventArgs e)
    {
        // One-shot: stop immediately so we don't refire if the action takes
        // longer than the interval (which it will — SetMode/TriggerOneShot
        // can be 5-30s on a cold Surface app).
        try { _scheduledFire?.Stop(); } catch { }

        var action = _scheduledAction;
        if (action == null) return;

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

                var err = action.Execute();
                if (err != null)
                    Logger.Error($"[ERR ] Scheduled {action.Describe()} FAILED: {err}");
                else
                    Logger.Error($"[INFO] Scheduled {action.Describe()} OK");
            }
            catch (Exception ex)
            {
                Logger.Error($"[ERR ] Scheduled {action.Describe()} CRASHED: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _suppressTopmostReassert = false;

                if (_autoExitAfterFire)
                {
                    // Auto-exit lets the device's real Sleep / Screen-off
                    // timeouts take over (overnight charging-then-sleep).
                    try
                    {
                        var first = _overlays.FirstOrDefault();
                        if (first != null && !first.IsDisposed)
                            first.BeginInvoke(() => { try { Exit(); } catch { } });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[ERR ] FakeSleep: auto-exit dispatch: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                else
                {
                    // HideWindow already pushed the Surface app to
                    // HWND_BOTTOM; just re-grab keyboard focus on the
                    // overlay so dismiss-on-key still works.
                    try
                    {
                        var first = _overlays.FirstOrDefault();
                        if (first != null && !first.IsDisposed)
                            first.BeginInvoke(() => { try { first.Activate(); } catch { } });
                    }
                    catch { }
                }
            }
        });
    }

    // ---- Exit ----------------------------------------------------------

    public static void Exit()
    {
        if (!_active) return;

        // Cancel any pending scheduled fire.
        try
        {
            _scheduledFire?.Stop();
            _scheduledFire?.Dispose();
            _scheduledFire = null;
            _scheduledAction = null;
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
        _autoExitAfterFire = false;
        _active = false;
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
