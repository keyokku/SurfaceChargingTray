using System.Runtime.InteropServices;

namespace SurfaceChargingTray;

/// <summary>
/// Launches a UWP / packaged app via the <c>shell:appsFolder\&lt;AUMID&gt;</c> protocol.
/// Uses Win32 <c>ShellExecuteW</c> directly — the same path PowerShell's
/// <c>Start-Process</c> uses, and the only one that doesn't hit
/// <c>E_ACCESSDENIED</c> when launching Microsoft-signed packages from a
/// non-elevated tray app.
/// </summary>
internal static class UwpLauncher
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ShellExecuteW(IntPtr hwnd, string lpVerb, string lpFile,
        string? lpParameters, string? lpDirectory, int nShowCmd);

    private const int SW_SHOWNORMAL = 1;

    /// <summary>Launches a UWP app by its AUMID. Throws on failure.</summary>
    public static void Launch(string aumid)
    {
        var result = ShellExecuteW(
            IntPtr.Zero, "open",
            $"shell:appsFolder\\{aumid}",
            null, null, SW_SHOWNORMAL);

        // ShellExecute returns an HINSTANCE; values <= 32 indicate failure.
        long code = result.ToInt64();
        if (code <= 32)
            throw new InvalidOperationException(
                $"Could not launch '{aumid}' via shell:appsFolder (ShellExecute code {code}). " +
                "The app may not be installed under that package name on this device.");
    }
}
