using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace SurfaceChargingTrayDiagnostic;

/// <summary>
/// Captures a comprehensive snapshot of the Surface app + this device:
///   - System fingerprint (Windows version, locale, device, .NET, etc.)
///   - Surface app package info + version
///   - Full UIA tree (unlimited depth, 1500-element cap, 30s budget)
///   - PrintWindow screenshot of the Surface app's main window
///   - Tool's own run log
/// All written to a .txt + .png pair next to this exe.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class DiagnosticRunner
{
    private const int MaxTreeElements = 1500;
    private const int TreeTimeoutMs   = 30_000;
    private const int ProcessWaitMs   = 30_000;

    public sealed record Result(
        bool Success,
        string TxtPath,
        string? PngPath,
        string Message,
        int ElementsCaptured);

    /// <summary>Run the diagnostic. <paramref name="progress"/> is invoked
    /// with short status strings on the UI thread.</summary>
    public static Result Run(Action<string> progress)
    {
        var log = new StringBuilder();
        void TLog(string msg)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss.fff");
            log.AppendLine($"[{ts}] {msg}");
        }

        var outDir = AppContext.BaseDirectory;
        var stamp  = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        var txtPath = Path.Combine(outDir, $"surface-diagnostic-{stamp}.txt");
        var pngPath = Path.Combine(outDir, $"surface-app-{stamp}.png");

        TLog("Diagnostic run starting.");
        progress("Resolving Surface app...");

        // ---- Surface app resolution ----
        SurfaceApp.Info? appInfo = null;
        try { appInfo = SurfaceApp.Resolve(); }
        catch (Exception ex) { TLog($"[ERR] SurfaceApp.Resolve: {ex.GetType().Name}: {ex.Message}"); }

        if (appInfo == null)
        {
            TLog("[ERR] No Surface app package found on this device.");
            var partial = ComposeOutput(log, appInfo, runningPid: null, treeText: null,
                elementsCount: 0, system: CollectSystemInfo(), settingsIni: TryReadSettingsIni(outDir));
            File.WriteAllText(txtPath, partial);
            return new Result(false, txtPath, null,
                "Surface app not installed on this device. Diagnostic saved.", 0);
        }
        TLog($"Surface app resolved: {appInfo.Aumid} (v{appInfo.Version})");

        // ---- Acquire process (launch if not running) ----
        progress("Finding Surface app process...");
        Process? proc = null;
        try
        {
            proc = SurfaceApp.FindRunningProcess();
            if (proc != null)
            {
                TLog($"Surface app already running, PID={proc.Id}, title='{SafeTitle(proc)}'");
            }
            else
            {
                TLog("Surface app not running; launching.");
                progress("Launching Surface app...");
                SurfaceApp.Launch(appInfo.Aumid);
                proc = SurfaceApp.WaitForProcess(ProcessWaitMs);
                if (proc == null)
                {
                    TLog("[ERR] Timed out waiting for Surface app process.");
                    var partial = ComposeOutput(log, appInfo, runningPid: null, treeText: null,
                        elementsCount: 0, system: CollectSystemInfo(), settingsIni: TryReadSettingsIni(outDir));
                    File.WriteAllText(txtPath, partial);
                    return new Result(false, txtPath, null,
                        "Launched the Surface app but couldn't find its window within 30 seconds. Diagnostic saved.", 0);
                }
                TLog($"Launched: PID={proc.Id}, title='{SafeTitle(proc)}'");
            }
        }
        catch (Exception ex)
        {
            TLog($"[ERR] Process acquisition: {ex.GetType().Name}: {ex.Message}");
        }

        // ---- Screenshot via PrintWindow ----
        string? pngOutPath = null;
        if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
        {
            try
            {
                progress("Capturing screenshot...");
                CaptureWindowToPng(proc.MainWindowHandle, pngPath);
                TLog($"Screenshot saved: {Path.GetFileName(pngPath)}");
                pngOutPath = pngPath;
            }
            catch (Exception ex)
            {
                TLog($"[ERR] Screenshot: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            TLog("[WARN] No window handle — skipping screenshot.");
        }

        // ---- UIA tree walk ----
        string? treeText = null;
        int elementsCount = 0;
        if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
        {
            try
            {
                progress("Scanning UI tree (this is the slow part)...");
                var win = AutomationElement.FromHandle(proc.MainWindowHandle);
                if (win == null)
                {
                    TLog("[ERR] AutomationElement.FromHandle returned null.");
                }
                else
                {
                    var sb = new StringBuilder();
                    var ctx = new WalkContext
                    {
                        Deadline = DateTime.Now.AddMilliseconds(TreeTimeoutMs),
                        Progress = progress
                    };
                    WalkElement(win, sb, depth: 0, ctx);
                    elementsCount = ctx.Count;
                    treeText = sb.ToString();
                    var elapsed = (DateTime.Now - (ctx.Deadline.AddMilliseconds(-TreeTimeoutMs))).TotalSeconds;
                    TLog($"Tree walk done: {elementsCount} elements in {elapsed:F1}s "
                       + $"(truncated={ctx.Truncated}, budget-expired={ctx.BudgetExpired}).");
                }
            }
            catch (Exception ex)
            {
                TLog($"[ERR] UIA tree walk: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- Compose final output ----
        progress("Writing diagnostic file...");
        var system = CollectSystemInfo();
        var settingsIni = TryReadSettingsIni(outDir);
        var output = ComposeOutput(log, appInfo, proc?.Id, treeText, elementsCount, system, settingsIni);
        try
        {
            File.WriteAllText(txtPath, output);
            TLog($"Diagnostic written: {Path.GetFileName(txtPath)}");
        }
        catch (Exception ex)
        {
            TLog($"[ERR] WriteAllText: {ex.GetType().Name}: {ex.Message}");
            return new Result(false, txtPath, pngOutPath,
                "Failed to write diagnostic file: " + ex.Message, elementsCount);
        }

        try { proc?.Dispose(); } catch { }

        return new Result(true, txtPath, pngOutPath,
            $"Captured {elementsCount} UIA elements.", elementsCount);
    }

    // ---- UIA tree walker ----------------------------------------------

    private sealed class WalkContext
    {
        public int Count;
        public DateTime Deadline;
        public bool Truncated;
        public bool BudgetExpired;
        public Action<string>? Progress;
        public int LastProgressCount;
    }

    private static void WalkElement(AutomationElement e, StringBuilder sb, int depth, WalkContext ctx)
    {
        if (ctx.Count >= MaxTreeElements) { ctx.Truncated = true; return; }
        if (DateTime.Now > ctx.Deadline)  { ctx.BudgetExpired = true; return; }

        string name = "", id = "", ct = "";
        try { name = (e.GetCurrentPropertyValue(AutomationElement.NameProperty)        as string ?? "").Trim(); } catch { }
        try { id   = (e.GetCurrentPropertyValue(AutomationElement.AutomationIdProperty) as string ?? "").Trim(); } catch { }
        try
        {
            if (e.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) is ControlType c)
                ct = c.ProgrammaticName?.Replace("ControlType.", "") ?? "";
        }
        catch { }

        bool isEnabled = true, isOffscreen = false;
        try { isEnabled   = (bool)e.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty); } catch { }
        try { isOffscreen = (bool)e.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty); } catch { }

        // Truncate large strings.
        if (name.Length > 80) name = name[..80] + "…";
        if (id.Length   > 60) id   = id[..60]   + "…";

        var indent = new string(' ', depth * 2);
        sb.Append(indent).Append("- [").Append(ct).Append("] name='").Append(name)
          .Append("' id='").Append(id).Append("'");
        if (!isEnabled)   sb.Append(" [disabled]");
        if (isOffscreen)  sb.Append(" [offscreen]");

        // List supported patterns — useful for understanding what kind of
        // control this actually is.
        var patterns = TryGetSupportedPatterns(e);
        if (!string.IsNullOrEmpty(patterns)) sb.Append(" patterns=").Append(patterns);
        sb.AppendLine();

        ctx.Count++;

        // Progress update every 100 elements.
        if (ctx.Count - ctx.LastProgressCount >= 100)
        {
            ctx.LastProgressCount = ctx.Count;
            try { ctx.Progress?.Invoke($"Scanning UI tree... ({ctx.Count} elements)"); } catch { }
        }

        // Walk children.
        try
        {
            var children = e.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement c in children)
            {
                if (ctx.Count >= MaxTreeElements || DateTime.Now > ctx.Deadline) break;
                WalkElement(c, sb, depth + 1, ctx);
            }
        }
        catch { }
    }

    private static string TryGetSupportedPatterns(AutomationElement e)
    {
        var names = new List<string>();
        AutomationPattern[] patterns;
        try { patterns = e.GetSupportedPatterns(); }
        catch { return ""; }
        foreach (var p in patterns)
        {
            try
            {
                var pn = p.ProgrammaticName ?? "";
                // ProgrammaticName looks like "SelectionItemPatternIdentifiers.Pattern"
                int dot = pn.IndexOf('.');
                if (dot > 0) pn = pn[..dot];
                pn = pn.Replace("PatternIdentifiers", "").Replace("Pattern", "");
                if (!string.IsNullOrEmpty(pn)) names.Add(pn);
            }
            catch { }
        }
        return names.Count == 0 ? "" : string.Join(",", names);
    }

    // ---- Screenshot via PrintWindow -----------------------------------

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // PW_RENDERFULLCONTENT — captures the window's actual rendered content
    // even for DWM-composited / GPU-accelerated windows (UWP apps).
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    private static void CaptureWindowToPng(IntPtr hwnd, string outPath)
    {
        if (!GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException("GetWindowRect failed.");
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"Invalid window bounds: {w}x{h}");

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
                    throw new InvalidOperationException("PrintWindow returned false.");
            }
            finally { g.ReleaseHdc(hdc); }
        }
        bmp.Save(outPath, ImageFormat.Png);
    }

    // ---- System info --------------------------------------------------

    private static string CollectSystemInfo()
    {
        var sb = new StringBuilder();
        // OS
        try
        {
            var os = Environment.OSVersion;
            sb.Append("Windows: ").Append(os.VersionString).AppendLine();
        }
        catch (Exception ex) { sb.AppendLine("Windows: (error " + ex.Message + ")"); }

        // Build / display name via WMI (more accurate than Environment.OSVersion)
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    sb.Append("Windows (WMI): ")
                      .Append(mo["Caption"]).Append(" — version ")
                      .Append(mo["Version"]).Append(" — build ")
                      .Append(mo["BuildNumber"]).AppendLine();
                    break;
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("Windows (WMI): error " + ex.Message); }

        // UI locale
        try
        {
            sb.Append("UI locale: ").Append(CultureInfo.CurrentUICulture.Name)
              .Append(" (").Append(CultureInfo.CurrentUICulture.DisplayName).AppendLine(")");
            sb.Append("Input locale: ").Append(CultureInfo.CurrentCulture.Name).AppendLine();
        }
        catch { }

        // Device manufacturer + model
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    sb.Append("Device: ").Append(mo["Manufacturer"]).Append(" — ").Append(mo["Model"]).AppendLine();
                    break;
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("Device: error " + ex.Message); }

        // CPU
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    sb.Append("CPU: ").Append(mo["Name"]).AppendLine();
                    break;
                }
            }
        }
        catch { }

        // Architecture
        try
        {
            sb.Append("OS architecture: ").Append(RuntimeInformation.OSArchitecture).AppendLine();
            sb.Append("Process architecture: ").Append(RuntimeInformation.ProcessArchitecture).AppendLine();
        }
        catch { }

        // .NET
        try
        {
            sb.Append(".NET: ").Append(RuntimeInformation.FrameworkDescription).AppendLine();
        }
        catch { }

        // Display + DPI
        try
        {
            var primary = Screen.PrimaryScreen;
            if (primary != null)
            {
                sb.Append("Primary screen: ").Append(primary.Bounds.Width).Append("x")
                  .Append(primary.Bounds.Height).Append(" @ ").Append(primary.BitsPerPixel).AppendLine(" bpp");
            }
            sb.Append("Screens total: ").Append(Screen.AllScreens.Length).AppendLine();
        }
        catch { }

        // Battery
        try
        {
            var ps = SystemInformation.PowerStatus;
            sb.Append("Battery: ").Append((int)(ps.BatteryLifePercent * 100)).Append("% (")
              .Append(ps.PowerLineStatus == PowerLineStatus.Online ? "plugged in" : "on battery")
              .AppendLine(")");
        }
        catch { }

        return sb.ToString();
    }

    // ---- Settings.ini scrape (if user dropped diagnostic next to main app) ----

    private static string? TryReadSettingsIni(string searchDir)
    {
        try
        {
            var path = Path.Combine(searchDir, "settings.ini");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        catch { }
        return null;
    }

    // ---- Output formatter ---------------------------------------------

    private static string ComposeOutput(
        StringBuilder log,
        SurfaceApp.Info? appInfo,
        int? runningPid,
        string? treeText,
        int elementsCount,
        string system,
        string? settingsIni)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=================================================================");
        sb.AppendLine("  Surface Charging Tray — Diagnostic Report");
        sb.AppendLine("=================================================================");
        sb.Append("Generated: ").AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
        sb.AppendLine("Tool version: 1.0.0");
        sb.AppendLine();

        sb.AppendLine("## System");
        sb.Append(system);
        sb.AppendLine();

        sb.AppendLine("## Surface App");
        if (appInfo == null)
        {
            sb.AppendLine("(no Surface app package was found on this device)");
        }
        else
        {
            sb.Append("AUMID:               ").AppendLine(appInfo.Aumid);
            sb.Append("Package family name: ").AppendLine(appInfo.PackageFamilyName ?? "(unknown)");
            sb.Append("Package full name:   ").AppendLine(appInfo.PackageFullName   ?? "(unknown)");
            sb.Append("Version:             ").AppendLine(appInfo.Version           ?? "(unknown)");
            sb.Append("Running PID:         ").AppendLine(runningPid?.ToString() ?? "(not running / not detected)");
        }
        sb.AppendLine();

        sb.AppendLine("## UIA Tree (exhaustive)");
        if (treeText == null)
        {
            sb.AppendLine("(tree could not be captured — see Tool Log below)");
        }
        else
        {
            sb.Append("Captured ").Append(elementsCount).AppendLine(" elements.");
            sb.AppendLine();
            sb.AppendLine(treeText);
        }
        sb.AppendLine();

        sb.AppendLine("## settings.ini (if present next to this tool)");
        if (string.IsNullOrEmpty(settingsIni))
        {
            sb.AppendLine("(no settings.ini found alongside this diagnostic tool)");
        }
        else
        {
            sb.AppendLine(settingsIni);
        }
        sb.AppendLine();

        sb.AppendLine("## Tool Log");
        sb.Append(log);
        sb.AppendLine();
        sb.AppendLine("=================================================================");
        sb.AppendLine("Where to send: post a comment on the diagnostic-results thread at");
        sb.AppendLine("  https://github.com/keyokku/SurfaceChargingTray/issues/2");
        sb.AppendLine("and attach this .txt file AND the matching .png screenshot.");
        sb.AppendLine("=================================================================");

        return sb.ToString();
    }

    private static string SafeTitle(Process p)
    {
        try { return p.MainWindowTitle; } catch { return "(unknown)"; }
    }
}
