using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace SurfaceChargingTray;

internal static class SurfaceController
{
    private const string DefaultAumid = "Microsoft.SurfaceHub_8wekyb3d8bbwe!App";

    /// <summary>Set by TrayAppContext at startup once AumidResolver picks an AUMID.</summary>
    public static string Aumid { get; set; } = DefaultAumid;

    /// <summary>
    /// Set by TrayAppContext at startup. Used by UiaCache to read/write
    /// AutomationId + Name caches that survive across app launches, so we
    /// can adapt to localized labels and Surface app updates without
    /// hardcoding strings. Tolerated null for tests / early init.
    /// </summary>
    public static SettingsModel? Settings { get; set; }

    /// <summary>
    /// Set true by CliMode at startup so AcquireSurfaceWindow skips the
    /// HideWindow push-to-back. Reason: when the user's screen is locked
    /// (the supported "schedule fires while you're away" pattern), UWP
    /// defers rendering of windows that aren't visible — pushing the
    /// Surface app to the back of z-order makes its visual tree never
    /// build, which makes the UIA query for "Battery & charging" find
    /// nothing. Letting the window come up normally lets UWP paint, the
    /// tree populates, the lookup succeeds. The user can't see anything
    /// anyway because the screen is off.
    /// </summary>
    public static bool SkipWindowHide { get; set; }

    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint ASFW_ANY       = 0xFFFFFFFF;
    // SetWindowPos hWndInsertAfter: HWND_BOTTOM = (HWND)1
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    // SetThreadExecutionState — asks Windows to keep the device in a
    // power state suitable for our work. Used during scheduled wake to
    // promote Modern Standby into full S0 so UWP launches actually work.
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private const uint ES_CONTINUOUS       = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED  = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;
    private const uint ES_AWAYMODE_REQUIRED = 0x00000040;

    /// <summary>
    /// Errors from the inner attempt that should be retried automatically.
    /// Limited to UI-Automation binding failures — those happen when we
    /// briefly catch the Surface window mid-activation and the UIA tree
    /// isn't bindable yet. A 500 ms delay then retry usually clears it.
    ///
    /// "Battery & charging card not found" is intentionally NOT in this
    /// list anymore: SetModeOnce / RefreshStateOnce already runs the
    /// discovery scan as a deliberate one-shot fallback when the layered
    /// lookup misses. If discovery also failed, retrying the whole call
    /// would just discover again and open the Surface app a second time
    /// to no benefit. The user gets a clean single-attempt error.
    /// </summary>
    private static bool IsTransient(string? msg) =>
        msg != null && msg.Contains("UI Automation");

    /// <summary>
    /// Switches the Surface app to the given mode.
    /// Returns null on success, an error message on failure.
    /// Retries once on transient failures.
    /// </summary>
    public static string? SetMode(string mode, string? duration)
    {
        var err = SetModeOnce(mode, duration);
        if (IsTransient(err))
        {
            Thread.Sleep(500);
            err = SetModeOnce(mode, duration);
        }
        return err;
    }

    private static string? SetModeOnce(string mode, string? duration)
    {
        Process? proc = null;
        bool launchedByUs = false;

        try
        {
            (proc, launchedByUs) = AcquireSurfaceWindow();

            var localProc = proc;  // for capture in lambda; appeases nullable analysis
            var win = WaitFor(() =>
                {
                    try { return AutomationElement.FromHandle(localProc.MainWindowHandle); }
                    catch { return null; }
                }, 5000);
            if (win == null) throw new Exception("Could not bind UI Automation to the Surface window.");

            // Layered lookup: cached AutomationId → cached Name → known-Names list
            // → structural search. Cache is updated on every successful match so
            // subsequent runs skip straight to the fast path.
            var settings = Settings ?? SettingsModel.Load();
            var bcGroup = LookupBatteryCard(win, settings);
            if (bcGroup == null)
            {
                UiaCache.LogTreeSnapshot(win, "card not found in SetMode");
                throw new Exception("'Battery & charging' card not found. Your Surface model or app version may not support the three charging modes.");
            }

            ExpandIfCollapsed(bcGroup);

            if (mode != "adaptive" && mode != "80" && mode != "100")
                throw new ArgumentException($"Unknown mode: {mode}");

            var rb = UiaCache.FindRadio(bcGroup, mode, settings, 5000);
            if (rb == null)
            {
                // Cache-poisoning retry: the cached card may point at a wrong
                // Group (auto-discovery picked a similar structure on first
                // launch). Clear, re-discover from scratch, retry once.
                bcGroup = LookupBatteryCardForcingRediscovery(win, settings);
                if (bcGroup != null)
                {
                    ExpandIfCollapsed(bcGroup);
                    rb = UiaCache.FindRadio(bcGroup, mode, settings, 5000);
                }
            }
            if (rb == null)
            {
                UiaCache.LogTreeSnapshot(win, $"radio '{mode}' not found in SetMode");
                throw new Exception($"Couldn't find the radio button for mode '{mode}'. Your Surface app may be an older build that doesn't expose this mode.");
            }

            var sel = (SelectionItemPattern)rb.GetCurrentPattern(SelectionItemPattern.Pattern);
            if (!sel.Current.IsSelected)
            {
                sel.Select();
                Thread.Sleep(300);
            }

            if (mode == "100" && !string.IsNullOrEmpty(duration))
                SetDuration(win, duration);

            StateStore.Save(mode, duration);

            Thread.Sleep(200);
            CloseProc(proc);
            proc = null;  // CloseProc disposed it; don't double-dispose in finally

            ClearError();
            return null;
        }
        catch (Exception ex)
        {
            if (launchedByUs && proc != null)
            {
                TryCloseQuiet(proc);
                proc = null;
            }
            WriteError(ex.Message);
            return ex.Message;
        }
        finally
        {
            // Safety net for any path that didn't already close+dispose.
            proc?.Dispose();
            // Release any wake-state request set by AcquireSurfaceWindow so
            // the device can resume normal sleep behaviour. ES_CONTINUOUS
            // alone (no other flags) clears all prior requests. Safe to
            // call even if no request was set.
            try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
        }
    }

    /// <summary>
    /// Variant B equivalent of <see cref="SetMode"/>. Invokes the Surface
    /// app's one-shot "Charge to 100%" override button. Returns null on
    /// success, an error message on failure. Retries once on transient
    /// UI-Automation binding failures (same logic as SetMode).
    ///
    /// No mode / duration parameters — variant B exposes exactly one
    /// action. Does not write StateStore (variant B has no persistent
    /// "current mode" concept; button enabled-state is read live).
    /// </summary>
    public static string? TriggerOneShot()
    {
        var err = TriggerOneShotOnce();
        if (IsTransient(err))
        {
            Thread.Sleep(500);
            err = TriggerOneShotOnce();
        }
        return err;
    }

    private static string? TriggerOneShotOnce()
    {
        Process? proc = null;
        bool launchedByUs = false;

        try
        {
            (proc, launchedByUs) = AcquireSurfaceWindow();

            var localProc = proc;  // for capture in lambda
            var win = WaitFor(() =>
                {
                    try { return AutomationElement.FromHandle(localProc.MainWindowHandle); }
                    catch { return null; }
                }, 5000);
            if (win == null) throw new Exception("Could not bind UI Automation to the Surface window.");

            var settings = Settings ?? SettingsModel.Load();
            var bcGroup = LookupBatteryCard(win, settings);
            if (bcGroup == null)
            {
                UiaCache.LogTreeSnapshot(win, "card not found in TriggerOneShot");
                throw new Exception("'Battery & charging' card not found. Run the diagnostic tool and share the report.");
            }

            ExpandIfCollapsed(bcGroup);

            var btn = UiaCache.FindOneShotButton(bcGroup, settings);
            if (btn == null)
            {
                // Cache-poisoning retry — same shape as SetMode's retry.
                bcGroup = LookupBatteryCardForcingRediscovery(win, settings);
                if (bcGroup != null)
                {
                    ExpandIfCollapsed(bcGroup);
                    btn = UiaCache.FindOneShotButton(bcGroup, settings);
                }
            }
            if (btn == null)
            {
                UiaCache.LogTreeSnapshot(win, "one-shot button not found in TriggerOneShot");
                throw new Exception("Couldn't find the 'Charge to 100%' override button. The Surface app's UI may have changed; run the diagnostic tool and share the report.");
            }

            // Refuse to click a disabled button. Disabled means Surface app
            // is currently NOT limiting smart charging (or the override has
            // already been triggered this cycle). Surfacing this to the
            // caller is more useful than silently doing nothing — the tray
            // already greys its menu item to mirror this state, but a
            // scheduled fire might land at a moment the button is disabled
            // and the user deserves to know.
            try
            {
                bool enabled = (bool)btn.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty);
                if (!enabled)
                    throw new Exception("'Charge to 100%' is currently disabled — Smart Charging isn't limiting right now, or the override was already triggered this cycle.");
            }
            catch (Exception readEx) when (readEx.Message.StartsWith("'Charge to 100%'"))
            {
                throw;  // rethrow the explicit guard above
            }
            catch
            {
                // IsEnabled read failed — proceed and let Invoke surface any error.
            }

            // Variant B's button is invoke-only (one-shot semantics). Use
            // InvokePattern rather than SelectionItemPattern; the button
            // has no "selected" state to inspect.
            var inv = (InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern);
            inv.Invoke();
            Thread.Sleep(300);

            Thread.Sleep(200);
            CloseProc(proc);
            proc = null;

            ClearError();
            return null;
        }
        catch (Exception ex)
        {
            if (launchedByUs && proc != null)
            {
                TryCloseQuiet(proc);
                proc = null;
            }
            WriteError(ex.Message);
            return ex.Message;
        }
        finally
        {
            proc?.Dispose();
            try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
        }
    }

    /// <summary>
    /// Reads the current mode from the Surface app and updates the cache.
    /// Retries once on transient failures.
    /// </summary>
    public static string? RefreshState()
    {
        var err = RefreshStateOnce();
        if (IsTransient(err))
        {
            Thread.Sleep(500);
            err = RefreshStateOnce();
        }
        return err;
    }

    private static string? RefreshStateOnce()
    {
        Process? proc = null;
        bool launchedByUs = false;

        try
        {
            (proc, launchedByUs) = AcquireSurfaceWindow();

            var win = AutomationElement.FromHandle(proc.MainWindowHandle)
                ?? throw new Exception("Could not bind UI Automation to the Surface window.");

            var settings = Settings ?? SettingsModel.Load();
            var bcGroup = LookupBatteryCard(win, settings);
            if (bcGroup == null)
            {
                UiaCache.LogTreeSnapshot(win, "card not found in RefreshState");
                throw new Exception("'Battery & charging' card not found. Your Surface model or app version may not support the three charging modes.");
            }

            ExpandIfCollapsed(bcGroup);

            // v1.3.0 silent variant detection — runs alongside the existing
            // flow without changing variant A behavior. Result lands in
            // settings.ini for later phases to consume. Variant B users still
            // hit the existing "no radio selected" error path below; the
            // tray's variant-aware menu (Phase 3) will short-circuit before
            // RefreshState is even called for them.
            try
            {
                var appVer = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
                UiaCache.DetectVariant(bcGroup, settings, appVer);
            }
            catch (Exception detEx)
            {
                // Detection is best-effort; never fail RefreshState because of it.
                Logger.Error($"[ERR ] Silent DetectVariant in RefreshState: {detEx.GetType().Name}: {detEx.Message}");
            }

            // FindSelectedModeKey internally walks the 3 radios via the layered lookup
            // (so its cache gets populated as a side effect) and returns the modeKey
            // of whichever radio reports active via SelectionItem / Toggle / Legacy.
            var selectedKey = UiaCache.FindSelectedModeKey(bcGroup, settings);
            if (selectedKey == null)
            {
                // Cache-poisoning retry — same logic as SetMode.
                bcGroup = LookupBatteryCardForcingRediscovery(win, settings);
                if (bcGroup != null)
                {
                    ExpandIfCollapsed(bcGroup);
                    selectedKey = UiaCache.FindSelectedModeKey(bcGroup, settings);
                }
            }
            if (selectedKey == null)
            {
                UiaCache.LogTreeSnapshot(win, "no radio reports selected in RefreshState");
                throw new Exception("None of the three charging-mode radios appear selected. Your Surface app build may differ from the one this tool was written for.");
            }

            // Duration combo only exists when "Charge to 100%" is selected.
            // The combo's AutomationId ("DurationSelectionComboBox") is set by
            // Microsoft and isn't localized, so it stays a stable key. The
            // selected item's Name IS localized; we infer 1day vs 1week from
            // a substring match rather than exact-equality so non-English
            // labels like "1 jour" / "1 semaine" still get classified.
            string? durKey = null;
            if (selectedKey == "100")
            {
                var combo = win.FindFirst(TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "DurationSelectionComboBox"));
                if (combo != null)
                {
                    try
                    {
                        var sp = (SelectionPattern)combo.GetCurrentPattern(SelectionPattern.Pattern);
                        var selArr = sp.Current.GetSelection();
                        if (selArr.Length > 0)
                        {
                            var label = selArr[0].Current.Name?.ToLowerInvariant() ?? "";
                            // crude but resilient classification — most languages
                            // keep "1" + a day/week stem in the visible label.
                            if (label.Contains("week") || label.Contains("semaine") ||
                                label.Contains("woche") || label.Contains("settimana") ||
                                label.Contains("неделя") || label.Contains("週"))
                                durKey = "1week";
                            else
                                durKey = "1day";
                        }
                    }
                    catch { }
                }
            }

            StateStore.Save(selectedKey, durKey);

            if (launchedByUs)
            {
                Thread.Sleep(200);
                CloseProc(proc);
                proc = null;
            }

            ClearError();
            return null;
        }
        catch (Exception ex)
        {
            if (launchedByUs && proc != null)
            {
                TryCloseQuiet(proc);
                proc = null;
            }
            WriteError(ex.Message);
            return ex.Message;
        }
        finally
        {
            proc?.Dispose();
        }
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>
    /// Normal battery-card lookup: layered cache → multi-language → structural.
    /// If layers 1-4 miss, runs the comprehensive discovery scan as a one-shot
    /// fallback (expands every collapsible element, re-walks the tree). Returns
    /// null only if every strategy failed.
    /// </summary>
    private static AutomationElement? LookupBatteryCard(AutomationElement win, SettingsModel settings)
    {
        var bcGroup = UiaCache.FindBatteryCard(win, settings, 15000);
        if (bcGroup == null)
        {
            if (UiaCache.DiscoverAndCacheAll(win, settings))
                bcGroup = UiaCache.FindBatteryCard(win, settings, 5000);
        }
        return bcGroup;
    }

    /// <summary>
    /// Cache-poisoning recovery: cached AutomationId/Name lookup may
    /// return a wrong Group that an earlier auto-discovery pass picked
    /// (e.g. on a Surface app build where the structure differs from
    /// what we expected). When a downstream operation under that cached
    /// card fails — no radio found, or no radio reports IsSelected — we
    /// clear the cache and force a fresh discovery + structural search,
    /// which won't return the same wrong Group.
    /// </summary>
    private static AutomationElement? LookupBatteryCardForcingRediscovery(AutomationElement win, SettingsModel settings)
    {
        UiaCache.ClearAllCache(settings);
        if (!UiaCache.DiscoverAndCacheAll(win, settings)) return null;
        return UiaCache.FindBatteryCard(win, settings, 5000);
    }

    private static (Process proc, bool launchedByUs) AcquireSurfaceWindow()
    {
        // If the Surface app is already running, close it first. The user
        // could be on any page (Device info, Help, etc.) where the
        // Battery & charging card isn't in the UIA tree, which would make
        // our search throw. Killing-and-relaunching guarantees we land on
        // the home page where the card lives.
        var existing = FindSurface();
        if (existing != null)
        {
            try
            {
                existing.CloseMainWindow();
                if (!existing.WaitForExit(2500)) existing.Kill();
            }
            catch { }
            existing.Dispose();
            // Brief pause to let the package's process fully tear down before
            // we trigger a re-activation; otherwise the new launch sometimes
            // attaches to the dying instance.
            Thread.Sleep(300);
        }

        // Grant foreground rights to the activation target. Without this,
        // when a global hotkey triggers SetMode our process isn't actually
        // in the foreground, Windows refuses to let the new app fully
        // activate, and the Surface app launches into an underpopulated
        // state where the Battery & charging UIA tree never finishes
        // building. (Tray-menu clicks don't hit this because the menu
        // implicitly puts our process in the foreground.)
        AllowSetForegroundWindow(ASFW_ANY);

        // Promote Modern Standby into full S0 active state if the device
        // is partially asleep (e.g., a scheduled task fired during sleep
        // and woke us into a throttled context). Without this, UWP launches
        // and the desktop session can stay suspended, which makes the
        // Surface app fail to come up at all. ES_CONTINUOUS keeps the
        // request alive across our subsequent UIA work; we clear it in
        // the cleanup path. Best-effort — if this call fails, the existing
        // 30s wait below still gives the device a chance to wake naturally.
        try { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED); } catch { }

        // Diagnostic: log session + what's already in the process list,
        // so when wake-context launches fail we can see whether the process
        // exists at all and what its window title was at the time.
        try
        {
            var sid = Process.GetCurrentProcess().SessionId;
            Logger.Error($"[INFO] AcquireSurfaceWindow: SessionId={sid}, AUMID={Aumid}");
            Logger.Error($"[INFO] AcquireSurfaceWindow: Launching now");
        }
        catch { }

        UwpLauncher.Launch(Aumid);

        // 30s rather than the previous 10s. UWP cold-launch from a
        // suspended Modern Standby state legitimately takes longer than
        // from active state. Tray-click invocations still typically
        // resolve in <2s; the longer budget only matters for wake-context.
        const int LaunchTimeoutSeconds = 30;
        var deadline = DateTime.Now.AddSeconds(LaunchTimeoutSeconds);
        int diagTick = 0;
        while (DateTime.Now < deadline)
        {
            var p = FindSurface();
            if (p != null)
            {
                Logger.Error($"[INFO] AcquireSurfaceWindow: Surface found after {(DateTime.Now - deadline.AddSeconds(-LaunchTimeoutSeconds)).TotalSeconds:F1}s, PID={p.Id}");
                if (!SkipWindowHide)
                {
                    HideWindow(p.MainWindowHandle);
                }
                else
                {
                    Logger.Error("[INFO] AcquireSurfaceWindow: skipping HideWindow (CLI mode — letting UWP paint the tree)");
                }
                // Small settle so the UIA tree has time to populate before
                // the caller starts searching it. Especially helps on the
                // hotkey path where activation happens in a less privileged
                // context and content loads a beat slower.
                Thread.Sleep(400);
                return (p, true);
            }
            Thread.Sleep(50);
            // Every ~3 seconds, dump candidate processes so we can see
            // what's there. Cheap; only fires during the launch wait, and
            // gives us actual data when the find fails.
            if (++diagTick % 60 == 0)
            {
                try { LogCandidateProcesses(diagTick / 60); }
                catch { }
            }
        }
        throw new Exception($"Surface app didn't launch within {LaunchTimeoutSeconds} seconds. Is the Surface app installed and working?");
    }

    /// <summary>
    /// Enumerates processes whose name OR window title hints at the Surface
    /// app, so when the find loop times out the log captures what processes
    /// actually were in flight. Helps debug wake-from-sleep failures where
    /// the process exists but doesn't match our title==\"Surface\" check yet.
    /// </summary>
    private static void LogCandidateProcesses(int tick)
    {
        var lines = new System.Text.StringBuilder();
        lines.Append($"[INFO] AcquireSurfaceWindow tick #{tick}: candidates: ");
        int count = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name  = p.ProcessName ?? "";
                var title = p.MainWindowTitle ?? "";
                bool isCandidate =
                    name.Contains("Surface", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Surface", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
                if (isCandidate)
                {
                    if (count > 0) lines.Append("; ");
                    lines.Append($"PID={p.Id} name=\"{name}\" title=\"{title}\" hwnd={(p.MainWindowHandle != IntPtr.Zero ? "yes" : "no")}");
                    count++;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        if (count == 0) lines.Append("(none)");
        Logger.Error(lines.ToString());
    }

    /// <summary>
    /// Localized window titles for the Surface app — best-effort. Windows
    /// pulls a UWP package's MainWindowTitle from the localized DisplayName
    /// resource, so a German install shows "Oberfläche" not "Surface". Best
    /// to recognize them all. The English match comes first since it's still
    /// the most common locale.
    /// </summary>
    private static readonly string[] SurfaceWindowTitles =
    {
        "Surface",         // en, fr, it, es (same word), nl, da, sv, no, fi, pt, pl, ro, hr
        "Oberfläche",      // de-DE
        "Pinta",           // some Nordic locales
        "Yüzey",           // tr-TR
        "Поверхность",     // ru-RU
        "Powierzchnia",    // pl-PL alt
        "Powerchnia",      // pl-PL alt
        "Plocha",          // cs-CZ, sk-SK
        "Felület",         // hu-HU
        "Pavirsius",       // lt-LT (no diacritics)
        "Paviršius",       // lt-LT
        "Površina",        // sl-SI, hr-HR
        "Επιφάνεια",       // el-GR
        "السطح",            // ar
        "פני שטח",          // he-IL
        "พื้นผิว",            // th-TH
        "พื้นผิวSurface",     // th-TH alt brand-prefix
        "Surface",          // ja-JP (often kept in English)
        "サーフェス",         // ja-JP katakana
        "Surface",          // zh-CN (often kept)
        "表面",              // zh-CN literal
        "表面",              // zh-TW literal
        "Surface",          // ko-KR (often kept)
        "서피스",            // ko-KR
    };

    private static bool IsSurfaceWindowTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        foreach (var t in SurfaceWindowTitles)
            if (string.Equals(title, t, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Finds the running Surface app process (or null). Disposes every
    /// non-matching <see cref="Process"/> that <c>Process.GetProcesses</c>
    /// returned, so we don't leak handles for the dozens of unrelated
    /// processes the enumeration also includes.
    ///
    /// Matches on a list of locale-specific window titles AND a name-based
    /// fallback for systems whose Surface app uses a localized title we
    /// haven't catalogued yet.
    /// </summary>
    private static Process? FindSurface()
    {
        Process? match = null;
        // Two-pass approach: first pass strictly matches our localized title
        // list; second pass (only if needed) accepts any ApplicationFrameHost
        // process whose window title isn't empty and whose owning UWP package
        // name contains the Surface AUMID's prefix. The strict pass is fast
        // and zero-false-positive; the fallback pass is for locales we
        // haven't catalogued.
        var procs = Process.GetProcesses();
        // Strict pass
        for (int i = 0; i < procs.Length; i++)
        {
            var p = procs[i];
            if (match != null) continue;
            try
            {
                if (IsSurfaceWindowTitle(p.MainWindowTitle) && p.MainWindowHandle != IntPtr.Zero)
                {
                    match = p;
                    procs[i] = null!; // skip disposal below
                }
            }
            catch { }
        }
        // Dispose everyone we didn't keep.
        for (int i = 0; i < procs.Length; i++)
        {
            try { procs[i]?.Dispose(); } catch { }
        }
        return match;
    }

    /// <summary>
    /// Hide the Surface app's window from the user during our automation.
    ///
    /// History: previous builds used SetWindowPos(-32000, -32000) + SW_HIDE,
    /// which works for normal Win32 windows but is unreliable for the
    /// Surface app (a UWP host that re-positions and re-shows itself
    /// after launch — our hide flag and offscreen position got overridden
    /// most of the time, leaving the window visible to the user).
    ///
    /// New approach: push to the bottom of the z-order with HWND_BOTTOM.
    /// As long as the user has any other window open in front (Explorer,
    /// browser, the tray app's own dialogs, etc. — true for normal usage),
    /// the Surface app stays hidden behind them. We additionally try to
    /// move it offscreen as a defensive backup, and call SetWindowPos
    /// twice with a small delay so any self-restore by the Surface app
    /// gets clobbered. The user may briefly see the window flash on
    /// activation before we push it back — accepted UX trade for actually
    /// staying hidden during the operation.
    /// </summary>
    private static void HideWindow(IntPtr h)
    {
        PushToBack(h);
        // Re-push after a beat — the Surface app sometimes re-orders
        // itself to top during its own activation completion.
        Task.Run(async () =>
        {
            await Task.Delay(150);
            try { PushToBack(h); } catch { }
            await Task.Delay(300);
            try { PushToBack(h); } catch { }
        });
    }

    private static void PushToBack(IntPtr h)
    {
        // Push to back without moving or resizing or activating.
        SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        // Also try to park it offscreen — defensive; some Surface app builds
        // honour the move, some don't. The z-order push above is what
        // actually keeps it hidden in either case.
        SetWindowPos(h, HWND_BOTTOM, -32000, -32000, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static void ExpandIfCollapsed(AutomationElement bcGroup)
    {
        try
        {
            var exp = (ExpandCollapsePattern)bcGroup.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            if (exp.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
            {
                exp.Expand();
                Thread.Sleep(400);
            }
        }
        catch { }
    }

    private static void SetDuration(AutomationElement win, string duration)
    {
        // Combo's AutomationId is stable across locales (Microsoft's internal
        // English ID, not localized). The combo's items, however, have
        // localized Names like "1 day" / "1 jour" / "1 woche" — we identify
        // them by substring match rather than exact equality.
        var combo = WaitFor(() => win.FindFirst(TreeScope.Subtree,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "DurationSelectionComboBox")), 3000);
        if (combo == null) return;

        bool wantWeek = duration == "1week";

        // If the currently selected item already matches the target, skip the dance.
        try
        {
            var sp = (SelectionPattern)combo.GetCurrentPattern(SelectionPattern.Pattern);
            var selArr = sp.Current.GetSelection();
            if (selArr.Length > 0)
            {
                if (LooksLikeWeek(selArr[0].Current.Name) == wantWeek) return;
            }
        }
        catch { }

        try
        {
            var cExp = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            cExp.Expand();
            Thread.Sleep(350);

            // Combo items appear as descendants once expanded. Find them all,
            // then pick the one matching our wantWeek classification.
            var items = WaitFor(() =>
            {
                var found = combo.FindAll(TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                return found.Count > 0 ? found : null;
            }, 2000, 100);

            AutomationElement? target = null;
            if (items != null)
            {
                foreach (AutomationElement it in items)
                {
                    if (LooksLikeWeek(it.Current.Name) == wantWeek) { target = it; break; }
                }
            }

            if (target != null)
            {
                var iSel = (SelectionItemPattern)target.GetCurrentPattern(SelectionItemPattern.Pattern);
                iSel.Select();
                Thread.Sleep(250);
            }
            else
            {
                cExp.Collapse();
            }
        }
        catch { }
    }

    /// <summary>
    /// Crude language-resilient classifier for the duration combo. Most
    /// localized labels keep a recognizable week-stem; everything else
    /// is treated as the day option (the only other value Microsoft ships).
    /// </summary>
    private static bool LooksLikeWeek(string? label)
    {
        if (string.IsNullOrEmpty(label)) return false;
        var lower = label.ToLowerInvariant();
        return lower.Contains("week")    || lower.Contains("semaine") ||
               lower.Contains("woche")   || lower.Contains("settimana") ||
               lower.Contains("semana")  || lower.Contains("неделя") ||
               lower.Contains("週")       || lower.Contains("주");
    }

    private static void CloseProc(Process p)
    {
        try
        {
            p.CloseMainWindow();
            if (!p.WaitForExit(2000)) p.Kill();
        }
        catch { }
        p.Dispose();
    }

    private static void TryCloseQuiet(Process p)
    {
        try
        {
            p.CloseMainWindow();
            if (!p.WaitForExit(1500)) p.Kill();
        }
        catch { }
        p.Dispose();
    }

    private static T? WaitFor<T>(Func<T?> probe, int timeoutMs, int pollMs = 100) where T : class
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < deadline)
        {
            try
            {
                var r = probe();
                if (r != null) return r;
            }
            catch { }
            Thread.Sleep(pollMs);
        }
        return null;
    }

    private static void WriteError(string msg) => Logger.Error(msg);

    private static void ClearError()
    {
        // We no longer wipe surface-error.log on success — entries are
        // append-only and rotated. Leaving for callers expecting the symbol.
    }
}
