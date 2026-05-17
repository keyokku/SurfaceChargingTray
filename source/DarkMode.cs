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

    // ---- Native control dark-theming (Win10 1809+) ---------------------

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct COMBOBOXINFO
    {
        public int cbSize;
        public RECT rcItem;
        public RECT rcButton;
        public int stateButton;
        public IntPtr hwndCombo;
        public IntPtr hwndEdit;
        public IntPtr hwndList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref COMBOBOXINFO pcbi);

    private const int WM_THEMECHANGED = 0x031A;

    /// <summary>
    /// Applies the "DarkMode_Explorer" uxtheme to a control's window. Native
    /// scrollbars rendered for that control (and its children that don't
    /// override) pick up the dark theme. Win10 1809+. Safe no-op on older
    /// builds — control just keeps its default theme.
    /// </summary>
    public static void ApplyDarkExplorerTheme(Control c)
    {
        if (!IsAppsDarkMode()) return;
        if (c == null || !c.IsHandleCreated) return;
        try
        {
            SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            SendMessage(c.Handle, WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>
    /// Applies the "DarkMode_CFD" uxtheme to a ComboBox's window — the drop-
    /// down arrow chrome paints in dark colors. ALSO walks GetComboBoxInfo
    /// to find the dropdown list's child HWND and applies "DarkMode_Explorer"
    /// so its scrollbar matches when the dropdown has more items than fit.
    /// Win10 1809+. Safe no-op on older builds.
    /// </summary>
    public static void ApplyDarkComboBoxTheme(ComboBox combo)
    {
        if (!IsAppsDarkMode()) return;
        if (combo == null || !combo.IsHandleCreated) return;
        try
        {
            SetWindowTheme(combo.Handle, "DarkMode_CFD", null);
            SendMessage(combo.Handle, WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);

            // Dark-theme the dropdown listbox's scrollbars. The listbox is a
            // separate HWND owned by the combo — we get it via GetComboBoxInfo.
            var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
            if (GetComboBoxInfo(combo.Handle, ref info) && info.hwndList != IntPtr.Zero)
            {
                SetWindowTheme(info.hwndList, "DarkMode_Explorer", null);
                SendMessage(info.hwndList, WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { }
    }

    /// <summary>
    /// Recursively applies the dark-explorer uxtheme to a control and all
    /// of its children. Skips controls where it would be visually wrong
    /// (Buttons, Labels — they handle themselves via FlatStyle/colors).
    /// Call AFTER the control tree has its handles created (e.g. from
    /// Form.HandleCreated or Form.Shown).
    /// </summary>
    public static void ApplyDarkExplorerThemeRecursive(Control root)
    {
        if (!IsAppsDarkMode() || root == null) return;
        // Apply to root itself so its own scrollbars (if any) pick up dark.
        ApplyDarkExplorerTheme(root);
        foreach (Control child in root.Controls)
        {
            ApplyDarkExplorerThemeRecursive(child);
        }
    }

    /// <summary>
    /// Hooks a ComboBox's DropDown event so that the internal listbox (which
    /// owns the dropdown's scrollbar) gets DarkMode_Explorer applied EVERY
    /// time the dropdown opens. Necessary because the listbox HWND can be
    /// (re)created lazily and a single one-shot SetWindowTheme call at
    /// construction time may miss it. Idempotent — Tag check prevents
    /// double-subscription.
    /// </summary>
    public static void HookComboBoxDropdownDarkTheme(ComboBox combo)
    {
        if (combo == null) return;
        if ((combo.Tag as string) == "dark-dropdown-hooked") return;
        combo.Tag = "dark-dropdown-hooked";
        combo.DropDown += (_, _) =>
        {
            if (!IsAppsDarkMode()) return;
            try
            {
                var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
                if (GetComboBoxInfo(combo.Handle, ref info) && info.hwndList != IntPtr.Zero)
                {
                    SetWindowTheme(info.hwndList, "DarkMode_Explorer", null);
                    SendMessage(info.hwndList, WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
        };
    }
}

/// <summary>
/// TabControl subclass that paints its own background in dark mode rather
/// than relying on owner-draw + Paint-event overlay (which fights native
/// chrome). Overrides WndProc to:
///  - Suppress WM_ERASEBKGND with the system's light brush
///  - On WM_PAINT, fill the entire client area dark first, then let
///    owner-draw handle the tab items
///  - Paint a thin dark band over the body border line Win11 still draws
///
/// Use this in place of plain TabControl anywhere dark-mode coverage matters.
/// </summary>
internal sealed class DarkTabControl : TabControl
{
    public Color DarkBackground   { get; set; } = Color.FromArgb(0x1F, 0x1F, 0x1F);
    public Color DarkTabFg        { get; set; } = Color.White;
    public Color DarkTabSelectedBg { get; set; } = Color.FromArgb(0x33, 0x33, 0x33);
    public Color DarkBorder       { get; set; } = Color.FromArgb(0x55, 0x55, 0x55);

    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_PAINT      = 0x000F;

    public DarkTabControl()
    {
        // Owner-draw the tab items themselves.
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool isSelected = e.Index == SelectedIndex;
        var rect = e.Bounds;
        using var bgBrush = new SolidBrush(isSelected ? DarkTabSelectedBg : DarkBackground);
        e.Graphics.FillRectangle(bgBrush, rect);
        if (isSelected)
        {
            using var borderPen = new Pen(DarkBorder);
            e.Graphics.DrawLine(borderPen, rect.Left, rect.Bottom - 1, rect.Right, rect.Bottom - 1);
        }
        if (e.Index >= 0 && e.Index < TabPages.Count)
        {
            var text = TabPages[e.Index].Text;
            TextRenderer.DrawText(e.Graphics, text, Font, rect, DarkTabFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Paint the entire client area dark first, BEFORE native chrome
        // (suppressed) and before owner-drawn tabs. This covers the strip
        // background beside/above the tabs AND the body border.
        using (var bgBrush = new SolidBrush(DarkBackground))
        {
            e.Graphics.FillRectangle(bgBrush, ClientRectangle);
        }

        // Subtle dark-grey border around the content area below the tab
        // strip — gives a hint of structure separating tabs from content
        // without the harsh light Win11 default.
        if (TabCount > 0)
        {
            var stripRect = GetTabRect(0);
            using var borderPen = new Pen(DarkBorder);
            // Horizontal line directly under the tab strip
            e.Graphics.DrawLine(borderPen,
                0, stripRect.Bottom,
                ClientRectangle.Width - 1, stripRect.Bottom);
            // Left, right, and bottom border around the content body
            var bodyRect = new Rectangle(
                0, stripRect.Bottom,
                ClientRectangle.Width - 1, ClientRectangle.Height - stripRect.Bottom - 1);
            e.Graphics.DrawRectangle(borderPen, bodyRect);
        }

        // Now manually invoke owner-draw for each visible tab.
        for (int i = 0; i < TabCount; i++)
        {
            var r = GetTabRect(i);
            if (e.ClipRectangle.IntersectsWith(r))
            {
                var state = (i == SelectedIndex) ? DrawItemState.Selected : DrawItemState.None;
                OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, r, i, state));
            }
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_ERASEBKGND)
        {
            // Suppress the system's default light-color erase. Our OnPaint
            // does its own background fill.
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }
}
