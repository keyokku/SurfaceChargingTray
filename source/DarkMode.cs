using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SurfaceChargingTray;

internal static class DarkMode
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr ordinal);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetPreferredAppModeDelegate(int appMode);

    /// <summary>Detects whether Windows apps theme is set to dark.</summary>
    public static bool IsAppsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            return v is int i && i == 0;
        }
        catch { return false; }
    }

    /// <summary>Detects whether Windows system theme (taskbar/tray) is dark.</summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("SystemUsesLightTheme");
            return v is int i && i == 0;
        }
        catch { return true; }
    }

    /// <summary>
    /// Tells uxtheme.dll that this app supports dark mode for standard controls.
    /// Uses the undocumented SetPreferredAppMode (ordinal 135) — stable since Win10 1903.
    /// </summary>
    public static void AllowDarkModeForApp()
    {
        try
        {
            var h = LoadLibrary("uxtheme.dll");
            if (h == IntPtr.Zero) return;
            var proc = GetProcAddress(h, (IntPtr)135);
            if (proc == IntPtr.Zero) return;
            var fn = Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(proc);
            // 0=Default, 1=AllowDark, 2=ForceDark, 3=ForceLight
            fn(IsAppsDarkMode() ? 2 : 0);
        }
        catch { }
    }

    /// <summary>Applies the immersive-dark title-bar style to a form's window.</summary>
    public static void ApplyToForm(Form form)
    {
        if (!IsAppsDarkMode()) return;
        int yes = 1;
        try { DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref yes, sizeof(int)); }
        catch { }
        try { DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref yes, sizeof(int)); }
        catch { }
    }
}
