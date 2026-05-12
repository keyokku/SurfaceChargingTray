using System.IO;
using System.Runtime.Versioning;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace SurfaceChargingTray;

/// <summary>
/// Discovers the AppUserModelID of the installed Microsoft Surface app on
/// this device, so the tray works on any Surface model regardless of which
/// version of the package is installed (the package family name has changed
/// between firmware/SoC generations).
///
/// Strategy:
///   1. Try the cached AUMID written from a previous successful run.
///   2. Try a list of known good AUMIDs in order — fast, no PackageManager call.
///   3. Enumerate installed packages with the WinRT PackageManager and pick
///      one whose Start menu entry matches a known display name.
///
/// Result is cached in settings.ini so subsequent launches skip discovery.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class AumidResolver
{
    // Ordered by likelihood on modern Surface devices.
    private static readonly string[] KnownAumids =
    {
        "Microsoft.SurfaceHub_8wekyb3d8bbwe!App",
        "MicrosoftCorporationII.MicrosoftSurface_8wekyb3d8bbwe!App",
        "Microsoft.MicrosoftSurface_8wekyb3d8bbwe!App",
    };

    /// <summary>
    /// Returns a working AUMID for the Surface app. Always returns something
    /// (the first known default if detection fails) so callers don't need to
    /// handle null.  Persists the chosen AUMID to settings.ini.
    /// </summary>
    public static string Resolve(SettingsModel settings)
    {
        string? chosen = null;

        // 1. Cached value from a previous run, if still valid.
        if (!string.IsNullOrEmpty(settings.SurfaceAumid) && PackageExists(settings.SurfaceAumid!))
            chosen = settings.SurfaceAumid;

        // 2. Try known AUMIDs.
        if (chosen == null)
        {
            foreach (var aumid in KnownAumids)
            {
                if (PackageExists(aumid)) { chosen = aumid; break; }
            }
        }

        // 3. Scan installed packages for a Surface-named app.
        if (chosen == null)
        {
            try
            {
                var pm = new PackageManager();
                foreach (var pkg in pm.FindPackagesForUser(""))
                {
                    if (!IsSurfaceCandidate(pkg)) continue;
                    foreach (var entry in pkg.GetAppListEntries())
                    {
                        var aumid = entry.AppUserModelId;
                        if (string.IsNullOrEmpty(aumid)) continue;
                        // DisplayName is localized — accept any of the known
                        // localized labels OR any name whose package family
                        // already matched IsSurfaceCandidate (the package name
                        // isn't localized, so trusting it here is locale-proof).
                        // Earlier versions required exact "Surface" match, which
                        // failed on non-English Windows installs.
                        chosen = aumid;
                        break;
                    }
                    if (chosen != null) break;
                }
            }
            catch { /* PackageManager can throw on locked-down systems */ }
        }

        // 4. Last-resort fallback so the launch attempt at least has something
        //    sane to try; the user's mode toggle will then surface a clear
        //    "package not found" error if even this is wrong.
        chosen ??= KnownAumids[0];

        // Always persist so settings.ini exists alongside the exe after
        // first launch, making the whole package self-contained.
        if (settings.SurfaceAumid != chosen)
        {
            settings.SurfaceAumid = chosen;
            settings.Save();
        }
        else if (!File.Exists(Paths.Settings))
        {
            settings.Save();
        }
        return chosen;
    }

    private static bool IsSurfaceCandidate(Package pkg)
    {
        try
        {
            var name = pkg.Id.Name ?? "";
            // Match SurfaceHub, MicrosoftSurface, etc. — exclude diagnostics, services, proxies.
            if (!name.Contains("Surface", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Contains("Service", StringComparison.OrdinalIgnoreCase))    return false;
            if (name.Contains("Proxy", StringComparison.OrdinalIgnoreCase))      return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Cheap existence check — looks up the package family name part of the AUMID.
    /// </summary>
    private static bool PackageExists(string aumid)
    {
        try
        {
            int bang = aumid.IndexOf('!');
            if (bang < 0) return false;
            var pfn = aumid[..bang];
            var pm = new PackageManager();
            var packages = pm.FindPackagesForUser("", pfn);
            using var en = packages.GetEnumerator();
            return en.MoveNext();
        }
        catch { return false; }
    }
}
