using System.Windows.Forms;

namespace SurfaceChargingTray;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _themeTimer;
    private readonly HotkeyManager _hotkeys = new();
    private readonly SynchronizationContext _ui;

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
        DarkMenu.ApplyTo(_menu);
        UpdateMenuFromCache();
        UpdateAutoStartCheck();
        ApplyHotkeys();

        _themeTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _themeTimer.Tick += (s, e) =>
        {
            ApplyTrayIcon();
            DarkMenu.ApplyTo(_menu);
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
        try { _miShowError.Image = Icons.ErrorRed().ToBitmap(); } catch { }
        _icon.ShowBalloonTip(5000, "Surface charging tray", msg.Length > 200 ? msg[..200] : msg, ToolTipIcon.Error);
    }

    private void ClearError()
    {
        _lastError = "";
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
        try
        {
            _icon.Icon = DarkMode.IsSystemDarkMode() ? Icons.PlugWhite() : Icons.PlugBlack();
        }
        catch { }
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
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
