using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SurfaceChargingTray;

/// <summary>
/// Registers global hotkeys via Win32 RegisterHotKey, listening through a
/// message-only NativeWindow so we don't need a visible form.
///
/// Hotkey strings use AHK syntax:
///   #  Win   !  Alt   ^  Ctrl   +  Shift   then a single key character
///   or a function-key name (F1..F12).  e.g. "^+1" = Ctrl+Shift+1.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT      = 0x0001;
    private const uint MOD_CONTROL  = 0x0002;
    private const uint MOD_SHIFT    = 0x0004;
    private const uint MOD_WIN      = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class HiddenWindow : NativeWindow
    {
        public Action<int>? OnHotkey;

        public HiddenWindow()
        {
            // Message-only window: parent = HWND_MESSAGE (-3). The window
            // never appears on screen; it only receives queued messages.
            //
            // IMPORTANT: ClassName is left null so WinForms auto-generates a
            // unique class name for this NativeWindow. Setting ClassName to a
            // built-in like "STATIC" poisons WinForms' WindowClass cache and
            // breaks subsequent control creation (e.g. Label) elsewhere in
            // the process with Win32Exception 1410 ("Class already exists").
            var cp = new CreateParams
            {
                Caption = "SCT_Hotkey",
                Parent  = new IntPtr(-3),    // HWND_MESSAGE
                Style   = 0,
                ExStyle = 0,
            };
            CreateHandle(cp);
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"Failed to create hotkey message window (Win32 {Marshal.GetLastWin32Error()}).");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) OnHotkey?.Invoke(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    private readonly HiddenWindow _wnd;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        _wnd = new HiddenWindow();
        _wnd.OnHotkey = id =>
        {
            if (_callbacks.TryGetValue(id, out var cb)) cb();
        };
    }

    public void Clear()
    {
        foreach (var id in _callbacks.Keys.ToList())
            UnregisterHotKey(_wnd.Handle, id);
        _callbacks.Clear();
    }

    /// <summary>
    /// Returns null on success, an error message describing the failure
    /// otherwise (parse error, or already-registered hotkey).
    /// </summary>
    public string? Register(string keyString, Action callback)
    {
        if (!TryParse(keyString, out var mods, out var vk))
            return $"Could not parse hotkey '{keyString}'.";

        int id = _nextId++;
        if (!RegisterHotKey(_wnd.Handle, id, mods | MOD_NOREPEAT, vk))
        {
            int err = Marshal.GetLastWin32Error();
            return $"'{HumanizeHotkey(keyString)}' could not be registered (Win32 error {err}). " +
                   "It may already be in use by another app.";
        }
        _callbacks[id] = callback;
        return null;
    }

    private static bool TryParse(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrEmpty(s)) return false;

        int i = 0;
        while (i < s.Length)
        {
            switch (s[i])
            {
                case '#': mods |= MOD_WIN;     i++; continue;
                case '!': mods |= MOD_ALT;     i++; continue;
                case '^': mods |= MOD_CONTROL; i++; continue;
                case '+': mods |= MOD_SHIFT;   i++; continue;
            }
            break;
        }
        var key = s[i..].Trim();
        if (key.Length == 0) return false;

        if (key.Length == 1)
        {
            vk = char.ToUpperInvariant(key[0]);
            return true;
        }

        vk = key.ToUpperInvariant() switch
        {
            "F1"  => 0x70, "F2"  => 0x71, "F3"  => 0x72, "F4"  => 0x73,
            "F5"  => 0x74, "F6"  => 0x75, "F7"  => 0x76, "F8"  => 0x77,
            "F9"  => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            "SPACE"  => 0x20,
            "TAB"    => 0x09,
            "ENTER"  => 0x0D, "RETURN" => 0x0D,
            "ESC"    => 0x1B, "ESCAPE" => 0x1B,
            _ => 0
        };
        return vk != 0;
    }

    private static string HumanizeHotkey(string s)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            switch (s[i])
            {
                case '#': parts.Add("Win");   i++; continue;
                case '!': parts.Add("Alt");   i++; continue;
                case '^': parts.Add("Ctrl");  i++; continue;
                case '+': parts.Add("Shift"); i++; continue;
            }
            break;
        }
        if (i < s.Length) parts.Add(s[i..].ToUpperInvariant());
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        Clear();
        try { _wnd.DestroyHandle(); } catch { }
    }
}
