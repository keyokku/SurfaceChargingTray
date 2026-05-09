using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SurfaceChargingTray;

internal static class AutoStart
{
    public static string ShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Surface Charging Tray.lnk");

    public static bool IsInstalled() => File.Exists(ShortcutPath);

    public static void Install()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the running executable path.");

        // Use IShellLink via COM to write the .lnk. The shortcut points at the
        // exe and uses the exe's own embedded ApplicationIcon (no separate .ico
        // file needed alongside).
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exePath);
        link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
        link.SetIconLocation(exePath, 0);
        link.SetDescription("Surface Charging Tray");

        var file = (IPersistFile)link;
        file.Save(ShortcutPath, false);
    }

    public static void Uninstall()
    {
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
            int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
