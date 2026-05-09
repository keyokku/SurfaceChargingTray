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

    private const int SW_HIDE = 0;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Switches the Surface app to the given mode.
    /// Returns null on success, an error message on failure.
    /// </summary>
    public static string? SetMode(string mode, string? duration)
    {
        Process? proc = null;
        bool launchedByUs = false;

        try
        {
            (proc, launchedByUs) = AcquireSurfaceWindow();

            var win = WaitFor(() =>
                {
                    try { return AutomationElement.FromHandle(proc.MainWindowHandle); }
                    catch { return null; }
                }, 5000);
            if (win == null) throw new Exception("Could not bind UI Automation to the Surface window.");

            var bcGroup = WaitFor(() => win.FindFirst(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.NameProperty, "Battery & charging")), 10000, 200);
            if (bcGroup == null)
                throw new Exception("'Battery & charging' card not found. Your Surface model or app version may not support the three charging modes.");

            ExpandIfCollapsed(bcGroup);

            string targetName = mode switch
            {
                "adaptive" => "Adaptive",
                "80"       => "Limit to 80%",
                "100"      => "Charge to 100%",
                _ => throw new ArgumentException($"Unknown mode: {mode}")
            };

            var rb = WaitFor(() => win.FindFirst(TreeScope.Subtree, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                new PropertyCondition(AutomationElement.NameProperty, targetName))), 5000);
            if (rb == null)
                throw new Exception($"Couldn't find the '{targetName}' radio button. Your Surface app may be an older build that doesn't expose this mode.");

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

            ClearError();
            return null;
        }
        catch (Exception ex)
        {
            if (launchedByUs && proc != null) TryCloseQuiet(proc);
            WriteError(ex.Message);
            return ex.Message;
        }
    }

    /// <summary>Reads the current mode from the Surface app and updates the cache.</summary>
    public static string? RefreshState()
    {
        Process? proc = null;
        bool launchedByUs = false;

        try
        {
            (proc, launchedByUs) = AcquireSurfaceWindow();

            var win = AutomationElement.FromHandle(proc.MainWindowHandle)
                ?? throw new Exception("Could not bind UI Automation to the Surface window.");

            var bcGroup = WaitFor(() => win.FindFirst(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.NameProperty, "Battery & charging")), 10000, 200);
            if (bcGroup == null)
                throw new Exception("'Battery & charging' card not found. Your Surface model or app version may not support the three charging modes.");

            ExpandIfCollapsed(bcGroup);

            string? selectedMode = null;
            foreach (var name in new[] { "Adaptive", "Limit to 80%", "Charge to 100%" })
            {
                var rb = win.FindFirst(TreeScope.Subtree, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                    new PropertyCondition(AutomationElement.NameProperty, name)));
                if (rb == null) continue;
                try
                {
                    var sp = (SelectionItemPattern)rb.GetCurrentPattern(SelectionItemPattern.Pattern);
                    if (sp.Current.IsSelected) { selectedMode = name; break; }
                }
                catch { }
            }
            if (selectedMode == null)
                throw new Exception("None of the three charging-mode radios appear selected. Your Surface app build may differ from the one this tool was written for.");

            string? selectedDuration = null;
            if (selectedMode == "Charge to 100%")
            {
                var combo = win.FindFirst(TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "DurationSelectionComboBox"));
                if (combo != null)
                {
                    try
                    {
                        var sp = (SelectionPattern)combo.GetCurrentPattern(SelectionPattern.Pattern);
                        var selArr = sp.Current.GetSelection();
                        if (selArr.Length > 0) selectedDuration = selArr[0].Current.Name;
                    }
                    catch { }
                }
            }

            string modeKey = selectedMode switch
            {
                "Adaptive"       => "adaptive",
                "Limit to 80%"   => "80",
                "Charge to 100%" => "100",
                _ => ""
            };
            string? durKey = selectedDuration switch
            {
                "1 day"  => "1day",
                "1 week" => "1week",
                _ => null
            };

            StateStore.Save(modeKey, durKey);

            if (launchedByUs)
            {
                Thread.Sleep(200);
                CloseProc(proc);
            }

            ClearError();
            return null;
        }
        catch (Exception ex)
        {
            if (launchedByUs && proc != null) TryCloseQuiet(proc);
            WriteError(ex.Message);
            return ex.Message;
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
            // Brief pause to let the package's process fully tear down before
            // we trigger a re-activation; otherwise the new launch sometimes
            // attaches to the dying instance.
            Thread.Sleep(300);
        }

        UwpLauncher.Launch(Aumid);

        var deadline = DateTime.Now.AddSeconds(10);
        while (DateTime.Now < deadline)
        {
            var p = FindSurface();
            if (p != null)
            {
                HideWindow(p.MainWindowHandle);
                return (p, true);
            }
            Thread.Sleep(50);
        }
        throw new Exception("Surface app didn't launch within 10 seconds. Is the Surface app installed and working?");
    }

    private static Process? FindSurface() =>
        Process.GetProcesses().FirstOrDefault(p =>
        {
            try { return p.MainWindowTitle == "Surface" && p.MainWindowHandle != IntPtr.Zero; }
            catch { return false; }
        });

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
        string durLabel = duration == "1week" ? "1 week" : "1 day";

        var combo = WaitFor(() => win.FindFirst(TreeScope.Subtree,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "DurationSelectionComboBox")), 3000);
        if (combo == null) return;

        string currentLabel = "";
        try
        {
            var sp = (SelectionPattern)combo.GetCurrentPattern(SelectionPattern.Pattern);
            var selArr = sp.Current.GetSelection();
            if (selArr.Length > 0) currentLabel = selArr[0].Current.Name;
        }
        catch { }

        if (currentLabel == durLabel) return;

        try
        {
            var cExp = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            cExp.Expand();
            Thread.Sleep(350);

            var item = WaitFor(() => win.FindFirst(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.NameProperty, durLabel)), 2000);
            if (item != null)
            {
                var iSel = (SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern);
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

    private static void CloseProc(Process p)
    {
        try
        {
            p.CloseMainWindow();
            if (!p.WaitForExit(2000)) p.Kill();
        }
        catch { }
    }

    private static void TryCloseQuiet(Process p)
    {
        try
        {
            p.CloseMainWindow();
            if (!p.WaitForExit(1500)) p.Kill();
        }
        catch { }
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

    private static void WriteError(string msg)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Paths.ErrorLog, msg);
        }
        catch { }
    }

    private static void ClearError()
    {
        try { if (File.Exists(Paths.ErrorLog)) File.Delete(Paths.ErrorLog); }
        catch { }
    }
}
