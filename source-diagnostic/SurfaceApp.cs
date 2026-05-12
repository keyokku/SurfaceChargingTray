using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace SurfaceChargingTrayDiagnostic;

/// <summary>
/// Discovers, launches, and locates the Microsoft Surface app on this device.
/// Standalone re-implementation of the main app's AumidResolver +
/// UwpLauncher + FindSurface logic, simplified for one-shot diagnostic use
/// (no caching, no settings.ini persistence, no fallback layers — just
/// "find it once, return what you found").
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class SurfaceApp
{
    // Known AUMIDs for the Surface app on different Surface generations.
    private static readonly string[] KnownAumids =
    {
        "Microsoft.SurfaceHub_8wekyb3d8bbwe!App",
        "MicrosoftCorporationII.MicrosoftSurface_8wekyb3d8bbwe!App",
        "Microsoft.MicrosoftSurface_8wekyb3d8bbwe!App",
    };

    // Localized window titles for the Surface app. UWP packaged apps' main
    // window title is the localized DisplayName resource, so a German install
    // shows "Oberfläche" not "Surface". Best-effort list.
    private static readonly string[] LocalizedSurfaceTitles =
    {
        "Surface",
        "Oberfläche",      // de-DE
        "Yüzey",           // tr-TR
        "Поверхность",     // ru-RU
        "Plocha",          // cs/sk
        "Felület",         // hu-HU
        "Površina",        // sl/hr
        "Επιφάνεια",       // el-GR
        "السطح",            // ar
        "פני שטח",          // he-IL
        "พื้นผิว",            // th-TH
        "サーフェス",         // ja-JP katakana
        "表面",              // zh literal
        "서피스",            // ko-KR
    };

    public sealed record Info(
        string Aumid,
        string? PackageFamilyName,
        string? PackageFullName,
        string? Version);

    /// <summary>Resolve the Surface app's AUMID + package info on this device.
    /// Returns null if no Surface package is installed.</summary>
    public static Info? Resolve()
    {
        // 1) Try known AUMIDs against the package manager.
        try
        {
            var pm = new PackageManager();
            foreach (var aumid in KnownAumids)
            {
                int bang = aumid.IndexOf('!');
                if (bang < 0) continue;
                var pfn = aumid[..bang];
                var packages = pm.FindPackagesForUser("", pfn);
                using var en = packages.GetEnumerator();
                if (en.MoveNext() && en.Current is { } pkg)
                {
                    return new Info(aumid, pkg.Id.FamilyName, pkg.Id.FullName, FormatVersion(pkg));
                }
            }
        }
        catch { /* PackageManager can throw on locked-down systems */ }

        // 2) Scan all packages for a Surface-named one.
        try
        {
            var pm = new PackageManager();
            foreach (var pkg in pm.FindPackagesForUser(""))
            {
                if (!IsSurfaceCandidate(pkg)) continue;
                foreach (var entry in pkg.GetAppListEntries())
                {
                    if (!string.IsNullOrEmpty(entry.AppUserModelId))
                    {
                        return new Info(entry.AppUserModelId, pkg.Id.FamilyName, pkg.Id.FullName, FormatVersion(pkg));
                    }
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>Launch the Surface app by AUMID. Throws on failure.</summary>
    public static void Launch(string aumid)
    {
        var result = ShellExecuteW(IntPtr.Zero, "open",
            $"shell:appsFolder\\{aumid}", null, null, SW_SHOWNORMAL);
        long code = result.ToInt64();
        if (code <= 32)
            throw new InvalidOperationException(
                $"ShellExecute failed (code {code}) launching '{aumid}'. " +
                "The Surface app may not be installed under that AUMID.");
    }

    /// <summary>
    /// Find the running Surface app process by window title. Tries the
    /// localized list. Returns null if not found.
    /// </summary>
    public static Process? FindRunningProcess()
    {
        Process? match = null;
        var procs = Process.GetProcesses();
        for (int i = 0; i < procs.Length; i++)
        {
            var p = procs[i];
            if (match == null)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && IsSurfaceTitle(p.MainWindowTitle))
                    {
                        match = p;
                        procs[i] = null!;
                    }
                }
                catch { }
            }
        }
        for (int i = 0; i < procs.Length; i++)
        {
            try { procs[i]?.Dispose(); } catch { }
        }
        return match;
    }

    /// <summary>Wait up to <paramref name="timeoutMs"/> for the Surface app
    /// process to appear with a window handle. Returns null on timeout.</summary>
    public static Process? WaitForProcess(int timeoutMs)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < deadline)
        {
            var p = FindRunningProcess();
            if (p != null) return p;
            Thread.Sleep(150);
        }
        return null;
    }

    private static bool IsSurfaceTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        foreach (var t in LocalizedSurfaceTitles)
            if (string.Equals(title, t, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsSurfaceCandidate(Package pkg)
    {
        try
        {
            var name = pkg.Id.Name ?? "";
            if (!name.Contains("Surface", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Contains("Service",    StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Contains("Proxy",      StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch { return false; }
    }

    private static string? FormatVersion(Package pkg)
    {
        try
        {
            var v = pkg.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch { return null; }
    }

    // ---- Win32 ---------------------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ShellExecuteW(IntPtr hwnd, string lpVerb, string lpFile,
        string? lpParameters, string? lpDirectory, int nShowCmd);

    private const int SW_SHOWNORMAL = 1;
}
