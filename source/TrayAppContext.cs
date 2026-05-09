using System.Windows.Forms;

namespace SurfaceChargingTray;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _themeTimer;
    private readonly HotkeyManager _hotkeys = new();
    private readonly SynchronizationContext _ui;

    // Icons + bitmap loaded once and re-used. Re-creating them every theme
    // tick (5s) leaks HICON / HBITMAP handles until the next GC cycle and
    // drifts working set up over long uptimes.
    private readonly System.Drawing.Icon   _iconWhite;
    private readonly System.Drawing.Icon   _iconBlack;
    private readonly System.Drawing.Bitmap _errorBitmap;

    // Last theme we actually pushed; lets the timer no-op when nothing
    // changed (the common case).
    private bool? _appliedSystemDark;
    private bool? _appliedAppsDark;

    private SettingsModel _settings;
    private string _lastError = "";
    private bool _busy = false;

    private readonly ToolStripMenuItem _miAdaptive;
    private readonly ToolStripMenuItem _mi80;
    private readonly ToolStripMenuItem _mi100Day;
    private readonly ToolStripMenuItem _mi100Week;
    private readonly ToolStripMenuItem _miRefresh;
    private readonly ToolStripMenuItem _miOpenApp;
    private readonly ToolStripMenuItem _miSettings;
    private readonly ToolStripMenuItem _miAutoStart;
    private readonly ToolStripMenuItem _miShowError;
    private readonly ToolStripMenuItem _miExit;

    public TrayAppContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = SettingsModel.Load();

        // Cache icons once. Each Icon owns a native HICON; allocating fresh
        // ones every 5 seconds previously bled handles until GC ran.
        _iconWhite = Icons.PlugWhite();
        _iconBlack = Icons.PlugBlack();
        // ToBitmap() copies the pixels into a managed Bitmap. We dispose
        // the source Icon immediately so its HICON is released right away.
        using (var ico = Icons.ErrorRed())
            _errorBitmap = ico.ToBitmap();

        // Detect the installed Surface app's AUMID dynamically. Always
        // returns something; persists to settings.ini.
        SurfaceController.Aumid = AumidResolver.Resolve(_settings);

        _menu = new ContextMenuStrip { ShowImageMargin = true };
        _miAdaptive  = new ToolStripMenuItem("Adaptive",                (Image?)null, (s, e) => StartSetMode("adaptive"));
        _mi80        = new ToolStripMenuItem("Limit to 80%",            (Image?)null, (s, e) => StartSetMode("80"));
        _mi100Day    = new ToolStripMenuItem("Charge to 100% (1 day)",  (Image?)null, (s, e) => StartSetMode("100", "1day"));
        _mi100Week   = new ToolStripMenuItem("Charge to 100% (1 week)", (Image?)null, (s, e) => StartSetMode("100", "1week"));
        _miRefresh   = new ToolStripMenuItem("Refresh status",          (Image?)null, (s, e) => StartRefresh());
        _miOpenApp   = new ToolStripMenuItem("Open Surface app",        (Image?)null, (s, e) => OpenSurfaceApp());
        _miSettings  = new ToolStripMenuItem("Settings...",             (Image?)null, (s, e) => ShowSettings());
        _miAutoStart = new ToolStripMenuItem("Run at Windows login",    (Image?)null, (s, e) => ToggleAutoStart());
        _miShowError = new ToolStripMenuItem("Show last error",         (Image?)null, (s, e) => ShowLastError());
        _miExit      = new ToolStripMenuItem("Exit",                    (Image?)null, (s, e) => ExitThread());

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _miAdaptive, _mi80, _mi100Day, _mi100Week,
            new ToolStripSeparator(),
            _miRefresh, _miOpenApp, _miSettings, _miAutoStart, _miShowError, _miExit
        });

        _icon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = "Surface Charging: ?",
            Visible = true
        };

        ApplyTrayIcon();
        ApplyMenuTheme();
        UpdateMenuFromCache();
        UpdateAutoStartCheck();
        ApplyHotkeys();

        _themeTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _themeTimer.Tick += (s, e) =>
        {
            ApplyTrayIcon();
            ApplyMenuTheme();
        };
        _themeTimer.Start();
    }

    // ---- Mode switching ------------------------------------------------

    private void StartSetMode(string mode, string? duration = null)
    {
        if (_busy) return;
        _busy = true;
        _icon.Text = "Surface Charging: switching...";

        Task.Run(() =>
        {
            var err = SurfaceController.SetMode(mode, duration);
            _ui.Post(_ =>
            {
                _busy = false;
                if (err != null) ReportError(err);
                else { ClearError(); UpdateMenuFromCache(); }
            }, null);
        });
    }

    private void StartRefresh()
    {
        if (_busy) return;
        _busy = true;
        _icon.Text = "Surface Charging: refreshing...";

        Task.Run(() =>
        {
            var err = SurfaceController.RefreshState();
            _ui.Post(_ =>
            {
                _busy = false;
                if (err != null) ReportError(err);
                else { ClearError(); UpdateMenuFromCache(); }
            }, null);
        });
    }

    // ---- Error surfacing -----------------------------------------------

    private void ReportError(string msg)
    {
        _lastError = msg;
        _icon.Text = ClampTooltip("Surface Charging: ERROR — right-click 'Show last error'");
        // Reuse the cached error bitmap; never re-allocate.
        try { _miShowError.Image = _errorBitmap; } catch { }
        _icon.ShowBalloonTip(5000, "Surface charging tray", msg.Length > 200 ? msg[..200] : msg, ToolTipIcon.Error);
    }

    private void ClearError()
    {
        _lastError = "";
        // Don't dispose _errorBitmap — it's a cached resource we may need
        // again. Just unlink it from the menu item.
        try { _miShowError.Image = null; } catch { }
    }

    private void ShowLastError()
    {
        if (string.IsNullOrEmpty(_lastError))
            MessageBox.Show("No errors recorded. Last action succeeded.",
                "Surface tray", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show(_lastError, "Last error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ---- Tray icon + menu state ---------------------------------------

    private void ApplyTrayIcon()
    {
        bool dark = DarkMode.IsSystemDarkMode();
        if (_appliedSystemDark == dark) return;   // no theme change since last tick
        _appliedSystemDark = dark;
        try { _icon.Icon = dark ? _iconWhite : _iconBlack; }
        catch { }
    }

    private void ApplyMenuTheme()
    {
        bool dark = DarkMode.IsAppsDarkMode();
        if (_appliedAppsDark == dark) return;
        _appliedAppsDark = dark;
        DarkMenu.ApplyTo(_menu);
    }

    private void UpdateMenuFromCache()
    {
        var st = StateStore.Load();
        _icon.Text = ClampTooltip("Surface Charging: " + LabelFor(st.Mode, st.Duration));

        _miAdaptive.Checked = st.Mode == "adaptive";
        _mi80.Checked       = st.Mode == "80";
        _mi100Day.Checked   = st.Mode == "100" && st.Duration == "1day";
        _mi100Week.Checked  = st.Mode == "100" && st.Duration == "1week";
    }

    private static string LabelFor(string? mode, string? duration) => mode switch
    {
        "adaptive" => "Adaptive",
        "80"       => "Limit to 80%",
        "100" when duration == "1day"  => "Charge to 100% (1 day)",
        "100" when duration == "1week" => "Charge to 100% (1 week)",
        "100"      => "Charge to 100%",
        _          => "?"
    };

    /// <summary>NotifyIcon.Text has a 127-char limit; clip just in case.</summary>
    private static string ClampTooltip(string s) => s.Length > 63 ? s[..63] : s;

    // ---- Other actions -------------------------------------------------

    private static void OpenSurfaceApp()
    {
        try { UwpLauncher.Launch(SurfaceController.Aumid); }
        catch { }
    }

    private void ShowSettings()
    {
        try
        {
            using var form = new SettingsForm(_settings);
            form.Saved = () =>
            {
                _settings = SettingsModel.Load();
                ApplyHotkeys();
                _icon.ShowBalloonTip(2000, "Surface charging tray", "Hotkeys updated.", ToolTipIcon.Info);
            };
            form.ShowDialog();
        }
        catch (Exception ex)
        {
            ReportError("Settings dialog crashed: " + ex);
        }
    }

    private void ToggleAutoStart()
    {
        try
        {
            if (AutoStart.IsInstalled())
            {
                AutoStart.Uninstall();
                _icon.ShowBalloonTip(2000, "Surface charging tray", "Auto-start disabled.", ToolTipIcon.Info);
            }
            else
            {
                AutoStart.Install();
                _icon.ShowBalloonTip(2000, "Surface charging tray", "Will start with Windows.", ToolTipIcon.Info);
            }
            UpdateAutoStartCheck();
        }
        catch (Exception ex)
        {
            ReportError("Auto-start toggle failed: " + ex.Message);
        }
    }

    private void UpdateAutoStartCheck() => _miAutoStart.Checked = AutoStart.IsInstalled();

    // ---- Hotkeys -------------------------------------------------------

    private void ApplyHotkeys()
    {
        _hotkeys.Clear();
        var actionMap = new Dictionary<string, Action>
        {
            { "adaptive",  () => _ui.Post(_ => StartSetMode("adaptive"), null)         },
            { "80",        () => _ui.Post(_ => StartSetMode("80"), null)               },
            { "100-1day",  () => _ui.Post(_ => StartSetMode("100", "1day"), null)      },
            { "100-1week", () => _ui.Post(_ => StartSetMode("100", "1week"), null)     },
            { "cycle",     () => _ui.Post(_ => CycleMode(), null)                      }
        };
        var failures = new List<string>();
        foreach (var (action, h) in _settings.Hotkeys)
        {
            if (!h.Enabled || string.IsNullOrEmpty(h.Key)) continue;
            if (!actionMap.TryGetValue(action, out var cb)) continue;
            var err = _hotkeys.Register(h.Key, cb);
            if (err != null) failures.Add(err);
        }
        if (failures.Count > 0)
            ReportError("Some hotkeys could not be registered:" + Environment.NewLine + "• "
                        + string.Join(Environment.NewLine + "• ", failures));
    }

    private void CycleMode()
    {
        if (_busy) return;
        var st = StateStore.Load();
        if (st.Mode == "adaptive")           StartSetMode("80");
        else if (st.Mode == "80")            StartSetMode("100", "1day");
        else if (st.Mode == "100" && st.Duration == "1day")  StartSetMode("100", "1week");
        else                                 StartSetMode("adaptive");
    }

    // ---- Cleanup -------------------------------------------------------

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _themeTimer.Stop();
            _themeTimer.Dispose();
            _hotkeys.Dispose();
            // Drop the icon reference before disposing _icon's source bitmaps,
            // so NotifyIcon doesn't hold a dangling pointer.
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _iconWhite.Dispose();
            _iconBlack.Dispose();
            _errorBitmap.Dispose();
        }
        base.Dispose(disposing);
    }
}
