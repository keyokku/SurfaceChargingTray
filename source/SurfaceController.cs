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

    private const int SW_HIDE = 0;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint ASFW_ANY       = 0xFFFFFFFF;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint dwProcessId);

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
            // → structural search for any Group with 3+ radios. Cache is updated on
            // every successful match so subsequent runs skip straight to the fast path.
            var settings = Settings ?? SettingsModel.Load();
            var bcGroup = UiaCache.FindBatteryCard(win, settings, 15000);
            if (bcGroup == null)
            {
                // Self-healing: comprehensive discovery scan as last resort.
                // Expands every collapsible element in the window, walks the
                // tree, captures the card + 3 radios into the cache. Slow
                // (~1-2s) but only fires when normal lookup fails — typically
                // first launch after a Surface app update changed the labels.
                if (UiaCache.DiscoverAndCacheAll(win, settings))
                    bcGroup = UiaCache.FindBatteryCard(win, settings, 5000);
            }
            if (bcGroup == null)
                throw new Exception("'Battery & charging' card not found. Your Surface model or app version may not support the three charging modes.");

            ExpandIfCollapsed(bcGroup);

            if (mode != "adaptive" && mode != "80" && mode != "100")
                throw new ArgumentException($"Unknown mode: {mode}");

            var rb = UiaCache.FindRadio(bcGroup, mode, settings, 5000);
            if (rb == null)
                throw new Exception($"Couldn't find the radio button for mode '{mode}'. Your Surface app may be an older build that doesn't expose this mode.");

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
            var bcGroup = UiaCache.FindBatteryCard(win, settings, 15000);
            if (bcGroup == null)
            {
                if (UiaCache.DiscoverAndCacheAll(win, settings))
                    bcGroup = UiaCache.FindBatteryCard(win, settings, 5000);
            }
            if (bcGroup == null)
                throw new Exception("'Battery & charging' card not found. Your Surface model or app version may not support the three charging modes.");

            ExpandIfCollapsed(bcGroup);

            // FindSelectedModeKey internally walks the 3 radios via the layered lookup
            // (so its cache gets populated as a side effect) and returns the modeKey
            // of whichever radio reports IsSelected.
            var selectedKey = UiaCache.FindSelectedModeKey(bcGroup, settings);
            if (selectedKey == null)
                throw new Exception("None of the three charging-mode radios appear selected. Your Surface app build may differ from the one this tool was written for.");

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

        UwpLauncher.Launch(Aumid);

        var deadline = DateTime.Now.AddSeconds(10);
        while (DateTime.Now < deadline)
        {
            var p = FindSurface();
            if (p != null)
            {
                HideWindow(p.MainWindowHandle);
                // Small settle so the UIA tree has time to populate before
                // the caller starts searching it. Especially helps on the
                // hotkey path where activation happens in a less privileged
                // context and content loads a beat slower.
                Thread.Sleep(400);
                return (p, true);
            }
            Thread.Sleep(50);
        }
        throw new Exception("Surface app didn't launch within 10 seconds. Is the Surface app installed and working?");
    }

    /// <summary>
    /// Finds the running Surface app process (or null). Disposes every
    /// non-matching <see cref="Process"/> that <c>Process.GetProcesses</c>
    /// returned, so we don't leak handles for the dozens of unrelated
    /// processes the enumeration also includes.
    /// </summary>
    private static Process? FindSurface()
    {
        Process? match = null;
        foreach (var p in Process.GetProcesses())
        {
            if (match == null)
            {
                try
                {
                    if (p.MainWindowTitle == "Surface" && p.MainWindowHandle != IntPtr.Zero)
                    {
                        match = p;
                        continue;  // keep alive, skip Dispose
                    }
                }
                catch { }
            }
            p.Dispose();
        }
        return match;
    }

    private static void HideWindow(IntPtr h)
    {
        SetWindowPos(h, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        ShowWindow(h, SW_HIDE);
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
