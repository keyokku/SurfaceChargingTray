using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace SurfaceChargingTray;

internal class SettingsForm : Form
{
    private readonly SettingsModel _model;
    private readonly bool _openOnScheduleTab;
    private TabControl _tabs = null!;
    private TabPage _tabSchedule = null!;
    // Save button is a field so UpdateScheduleStatus can disable it when
    // the schedule time is incomplete. Inline validation > modal dialog.
    private Button _btnSave = null!;
    // "Run at Windows login" checkbox lives in the Settings dialog itself
    // (parked here so the tray menu stays focused on charging actions).
    // Its initial value reflects the current AutoStart registry state;
    // on Save we install/uninstall accordingly.
    private CheckBox _autoStartCheck = null!;
    public Action? Saved { get; set; }

    private class Row
    {
        public string Action = "";
        public CheckBox Enable = null!;
        public CheckBox Win = null!, Alt = null!, Ctrl = null!, Shift = null!;
        public ComboBox Key = null!;
    }

    private readonly List<Row> _rows = new();

    // ---- Variant awareness (v1.3.0+) ---------------------------------
    //
    // Snapshot of the device's detected UI variant at dialog construction.
    // Drives which hotkey rows are visible (variant A: mode rows; variant
    // B: oneshot row), the info banner text, and the schedule tab shape
    // (Phase 5). 'Re-detect device' button overwrites _model.DetectedVariant
    // in-memory and updates the banner; the user must reopen the dialog to
    // see updated row visibility (avoids the complexity of re-rendering
    // mid-dialog).
    private SurfaceUiVariant _variant = SurfaceUiVariant.Unknown;
    private Label _variantBanner = null!;
    private Button _btnRedetect = null!;

    private static SurfaceUiVariant ParseVariantString(string? s) => s switch
    {
        "A" => SurfaceUiVariant.A,
        "B" => SurfaceUiVariant.B,
        _   => SurfaceUiVariant.Unknown
    };

    // ---- Schedule controls (live in the Schedule tab) ------------------
    private ComboBox _scheduleMode    = null!;
    private ComboBox _scheduleDuration = null!;
    private Label    _scheduleDurationLabel = null!;
    // Two dropdowns instead of a TextBox: prevents user error on bad
    // HH:MM input. -1 (no selection) on either combo = "schedule cleared".
    private ComboBox _scheduleHour    = null!;
    private ComboBox _scheduleMinute  = null!;
    private Label    _scheduleStatus  = null!;
    private RadioButton _autoExitStay = null!;
    private RadioButton _autoExitYes  = null!;
    private Row      _scheduleHotkeyRow = null!;

    public SettingsForm(SettingsModel model, bool openOnScheduleTab = false)
    {
        _model = model;
        _openOnScheduleTab = openOnScheduleTab;
        // First-launch default (DetectedVariant null) is variant A — the
        // overwhelmingly common case for existing v1.2.x upgraders. The
        // tray's RefreshState writes the real variant to settings.ini on
        // first interaction; the next time the user opens this dialog the
        // banner + row visibility reflect the true variant.
        _variant = ParseVariantString(_model.DetectedVariant);
        if (_variant == SurfaceUiVariant.Unknown && string.IsNullOrEmpty(_model.DetectedVariant))
            _variant = SurfaceUiVariant.A;

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = new Font("Segoe UI", 9.5f);
        BuildUI();
        HandleCreated += (_, _) => DarkMode.ApplyToForm(this);
        Shown += (_, _) =>
        {
            // Force handle creation on EVERY TabPage so non-active tabs'
            // children have valid HWNDs when ApplyTheme runs — without this,
            // SetWindowTheme silently skips the inactive tab's controls and
            // their native scrollbars stay light until the user clicks the
            // tab (and even then often don't re-apply). Reading .Handle on
            // a TabPage creates the page and all its children. Cheap,
            // happens once.
            foreach (TabPage tp in _tabs.TabPages) { var _ = tp.Handle; }
            ApplyTheme();
            // Belt-and-suspenders: re-apply theme when the user switches
            // tabs. Some uxtheme transitions get lost on first paint of a
            // tab that was previously hidden.
            _tabs.SelectedIndexChanged += (_, _) => ApplyTheme();
            if (_openOnScheduleTab) _tabs.SelectedTab = _tabSchedule;
            // Re-apply status color after theme settles — DarkMode.IsAppsDarkMode
            // resolves correctly here, and the user's saved state may also need
            // the Save button enable/disable refreshed.
            UpdateScheduleStatus();
        };
    }

    private void BuildUI()
    {
        Text = "Surface Charging Tray — Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 820);
        MinimumSize = new Size(780, 720);
        Padding = new Padding(28);

        // ----- Bottom: footer (buttons + ko-fi link) -------------------
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0, 16, 0, 0)
        };

        var btnPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12)
        };
        _btnSave      = new Button { Text = "Save",   Size = new Size(110, 36), Margin = new Padding(0, 0, 12, 0) };
        var btnCancel = new Button { Text = "Cancel", Size = new Size(110, 36) };
        _btnSave.Click  += (_, _) => SaveAndClose();
        btnCancel.Click += (_, _) => Close();
        btnPanel.Controls.Add(_btnSave);
        btnPanel.Controls.Add(btnCancel);
        AcceptButton = _btnSave;
        CancelButton = btnCancel;
        footer.Controls.Add(btnPanel);

        var linksRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0), WrapContents = false
        };

        var koFiLink = new LinkLabel
        {
            Text = "If you liked this, consider a tip (ko-fi).",
            AutoSize = true,
            Font = new Font("Segoe UI", 8f),
            LinkArea = new LinkArea(30, 11),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 12, 0)
        };
        koFiLink.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://ko-fi.com/keyokku") { UseShellExecute = true }); }
            catch { }
        };

        var githubLink = new LinkLabel
        {
            Text = "For future updates and versions, check GitHub Page.",
            AutoSize = true,
            Font = new Font("Segoe UI", 8f),
            // LinkArea: "GitHub Page" is 11 chars starting after "For future updates and versions, check " (39 chars).
            LinkArea = new LinkArea(39, 11),
            ForeColor = SystemColors.GrayText
        };
        githubLink.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/keyokku/SurfaceChargingTray") { UseShellExecute = true }); }
            catch { }
        };

        linksRow.Controls.Add(koFiLink);
        linksRow.Controls.Add(githubLink);
        footer.Controls.Add(linksRow);

        // ----- Middle: TabControl with General + Schedule tabs ----------
        // 'General' (was 'Hotkeys' through v1.3.0) holds run-at-login, the
        // variant banner with Re-detect, and the hotkey grid. Renamed in
        // v1.3.1 to reflect that it has more than just hotkeys now.
        // Use DarkTabControl (which subclasses TabControl) so the entire
        // tab strip area paints dark via owner-draw + WndProc, instead of
        // fighting native chrome via Paint-event overlays.
        _tabs = DarkMode.IsAppsDarkMode()
            ? new DarkTabControl { Dock = DockStyle.Fill, Padding = new Point(14, 8) }
            : new TabControl    { Dock = DockStyle.Fill, Padding = new Point(14, 8) };
        var tabGeneral  = new TabPage("General")  { Padding = new Padding(16) };
        _tabSchedule    = new TabPage("Schedule") { Padding = new Padding(16) };
        BuildHotkeysTab(tabGeneral);
        BuildScheduleTab(_tabSchedule);
        _tabs.TabPages.Add(tabGeneral);
        _tabs.TabPages.Add(_tabSchedule);

        // Order matters: docked controls added BEFORE the Fill control so
        // the Fill claims only the remaining space.
        Controls.Add(_tabs);  // Fill
        Controls.Add(footer); // Bottom
    }

    // ---- Hotkeys tab ---------------------------------------------------

    private void BuildHotkeysTab(TabPage tab)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 16)
        };

        // Variant banner + Re-detect button (v1.3.0+) — gives the user a
        // visible record of which UI variant their device was classified as,
        // and a way to force a fresh detection if their Surface app updated.
        var variantRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12)
        };
        variantRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        variantRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _variantBanner = new Label
        {
            Text = BuildVariantBannerText(),
            AutoSize = true, MaximumSize = new Size(620, 0),
            ForeColor = SystemColors.GrayText, Tag = "gray",
            Margin = new Padding(0, 8, 16, 0)
        };
        _btnRedetect = new Button
        {
            // 40px tall (was 32) — DPI scaling on high-DPI Surface displays
            // was clipping the text descender of 'Re-detect device' at 32px.
            Text = "Re-detect device", Size = new Size(170, 40),
            Margin = new Padding(0, 4, 0, 0)
        };
        _btnRedetect.Click += (_, _) => RunRedetect();
        variantRow.Controls.Add(_variantBanner, 0, 0);
        variantRow.Controls.Add(_btnRedetect,   1, 0);
        header.Controls.Add(variantRow);

        // General settings (live above hotkeys so they're seen first).
        _autoStartCheck = new CheckBox
        {
            Text = "Run at Windows login",
            AutoSize = true,
            Checked = AutoStart.IsInstalled(),
            Margin = new Padding(0, 0, 0, 12)
        };
        header.Controls.Add(_autoStartCheck);
        header.Controls.Add(new Label
        {
            Text = "—", AutoSize = true,
            ForeColor = SystemColors.GrayText, Tag = "gray",
            Margin = new Padding(0, 0, 0, 8)
        });

        header.Controls.Add(new Label
        {
            Text = "Hotkeys — uncheck to disable. Pick modifiers and a key for each action.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 6)
        });
        header.Controls.Add(new Label
        {
            Text = "Tip: avoid Alt+Shift (Windows uses it for input language) and plain Win+digit (taskbar slots).",
            AutoSize = true, ForeColor = SystemColors.GrayText,
            Tag = "gray"
        });

        var grid = MakeHotkeyGrid();

        var keyChoices = BuildKeyChoices();
        int rowIdx = 1;

        // Variant-aware "Charging modes" section. Variant A users see the
        // five-row classic set; variant B users see only the oneshot row
        // (their device only exposes that one action); Unknown shows no
        // charging rows at all (the user should run the diagnostic tool
        // before binding hotkeys to actions that may not work).
        if (_variant == SurfaceUiVariant.A)
        {
            rowIdx = AddSectionHeader(grid, rowIdx, "Charging modes");
            rowIdx = AddHotkeyRow(grid, rowIdx, keyChoices, "adaptive",  "Adaptive");
            rowIdx = AddHotkeyRow(grid, rowIdx, keyChoices, "80",        "Limit to 80%");
            rowIdx = AddHotkeyRow(grid, rowIdx, keyChoices, "100-1day",  "Charge to 100% (1 day)");
            rowIdx = AddHotkeyRow(grid, rowIdx, keyChoices, "100-1week", "Charge to 100% (1 week)");
            rowIdx = AddHotkeyRow(grid, rowIdx, keyChoices, "cycle",     "Cycle through charging modes");
        }
        else if (_variant == SurfaceUiVariant.B)
        {
            rowIdx = AddSectionHeader(grid, rowIdx, "Charging override");
            rowIdx = AddHotkeyRow(grid, rowIdx, keyChoices, "oneshot",   "Charge to 100% (one-shot override)");
        }
        // Unknown: no charging-rows section — banner explains why.

        rowIdx = AddSectionHeader(grid, rowIdx, "Windows Power mode");
        rowIdx = AddHotkeyRow(grid, rowIdx, keyChoices, "power-efficient", "Best power efficiency");
        rowIdx = AddHotkeyRow(grid, rowIdx, keyChoices, "power-balanced",  "Balanced");
        rowIdx = AddHotkeyRow(grid, rowIdx, keyChoices, "power-perf",      "Best performance");

        // NOTE: "schedule-toggle" is intentionally NOT rendered in this tab —
        // it lives next to the schedule controls in the Schedule tab so the
        // user perceives them as one feature.

        var middle = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0) };
        middle.Controls.Add(grid);

        tab.Controls.Add(middle); // Fill
        tab.Controls.Add(header); // Top
    }

    private static TableLayoutPanel MakeHotkeyGrid()
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 6,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));

        grid.Controls.Add(MakeHeaderLabel("Action"), 0, 0);
        grid.Controls.Add(MakeHeaderLabel("Win"),    1, 0);
        grid.Controls.Add(MakeHeaderLabel("Alt"),    2, 0);
        grid.Controls.Add(MakeHeaderLabel("Ctrl"),   3, 0);
        grid.Controls.Add(MakeHeaderLabel("Shift"),  4, 0);
        grid.Controls.Add(MakeHeaderLabel("Key"),    5, 0);

        return grid;
    }

    // ---- Schedule tab --------------------------------------------------

    private void BuildScheduleTab(TabPage tab)
    {
        // Dock=Top + AutoSize lets the panel grow to its content height,
        // and the outer scroll Panel (added at the bottom of this method)
        // scrolls when the content exceeds the visible tab area.
        // Dock=Fill would force the panel to match the parent's height and
        // suppress overflow, which is what caused the Toggle-hotkey section
        // to be invisible/unreachable.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };

        // ---- Header explanation -----
        root.Controls.Add(new Label
        {
            Text = "Schedule charging mode switch when you step away from your device.",
            AutoSize = true, MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 0, 0, 14)
        });

        root.Controls.Add(new Label
        {
            Text = "HOW TO USE:",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        });

        // Numbered steps in a single Label so the lines stack tightly.
        // Explicit \n line-breaks; AutoSize + MaximumSize handles any wrap
        // if a step were ever long enough to overflow.
        root.Controls.Add(new Label
        {
            Text = "1. Schedule time & settings\n"
                 + "2. Activate hotkey, toggle black screen\n"
                 + "3. Leave device, DO NOT SLEEP/CLOSE/LOCK\n"
                 + "4. Auto wake / click any key to \"wake\"",
            AutoSize = true, MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 0, 0, 14)
        });

        root.Controls.Add(new Label
        {
            Text = "Only works plugged in. Use when necessary. "
                 + "Surface charge modes cannot switch while sleeping/locked/off.",
            AutoSize = true, MaximumSize = new Size(820, 0),
            ForeColor = SystemColors.GrayText, Tag = "gray",
            Margin = new Padding(0, 0, 0, 18)
        });

        // ---- Fire spec (mode + duration + time) ----
        root.Controls.Add(MakeSectionLabel("When to fire"));

        var fireGrid = new TableLayoutPanel
        {
            ColumnCount = 4, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 4, 0, 16)
        };
        // Col 0: label.  Col 1: input control (mode dropdown / time TextBox).
        // Col 2: duration label OR Clear button — widened to 140 so the
        // Clear button + its right-margin fit without clipping.
        // Col 3: duration dropdown / status text.
        fireGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        fireGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        fireGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        fireGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Mode row — variant-aware. Variant A: dropdown of all four
        // choices ((none) / Adaptive / 80% / 100%). Variant B: dropdown
        // is locked to a single 'Charge to 100% (one-shot override)'
        // entry, since the device only exposes that one action. Either
        // way the control is the same _scheduleMode ComboBox so the
        // existing save/load plumbing keeps working without forking.
        fireGrid.Controls.Add(MakeFieldLabel("Charging mode:"), 0, 0);
        _scheduleMode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6)
        };
        if (_variant == SurfaceUiVariant.B)
        {
            _scheduleMode.Items.AddRange(new object[]
            {
                "(none)", "Charge to 100% (one-shot)"
            });
        }
        else
        {
            _scheduleMode.Items.AddRange(new object[]
            {
                "(none)", "Adaptive", "Limit to 80%", "Charge to 100%"
            });
        }
        _scheduleMode.SelectedIndexChanged += (_, _) => UpdateDurationVisibility();
        fireGrid.Controls.Add(_scheduleMode, 1, 0);

        // Duration label + dropdown (variant A only — variant B's one-shot
        // doesn't have a duration concept; the button is fire-and-forget
        // for a single charge cycle). Created in both variants to keep
        // the field references non-null for theming + visibility toggling,
        // but Visible=false on B.
        _scheduleDurationLabel = MakeFieldLabel("Duration:");
        fireGrid.Controls.Add(_scheduleDurationLabel, 2, 0);
        _scheduleDuration = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 130, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6)
        };
        _scheduleDuration.Items.AddRange(new object[] { "1 day", "1 week" });
        fireGrid.Controls.Add(_scheduleDuration, 3, 0);

        // Time row: HH ComboBox + ":" + MM ComboBox in a small flow panel.
        // Dropdowns prevent the user from typing an invalid time.
        fireGrid.Controls.Add(MakeFieldLabel("Scheduled time (HH:MM):"), 0, 1);

        var timePanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 4),
            WrapContents = false
        };
        _scheduleHour = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 64, Margin = new Padding(0, 2, 6, 2)
        };
        for (int h = 0; h < 24; h++) _scheduleHour.Items.Add(h.ToString("00"));
        _scheduleHour.SelectedIndexChanged += (_, _) =>
        {
            // Auto-fill minute to 00 when hour is picked without a minute —
            // most overnight schedules land on the hour, and this means the
            // user only has to make one pick instead of two for the common
            // case. They can still change minute afterward.
            if (_scheduleHour.SelectedIndex >= 0 && _scheduleMinute.SelectedIndex < 0)
                _scheduleMinute.SelectedIndex = 0;
            UpdateScheduleStatus();
        };

        _scheduleMinute = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 64, Margin = new Padding(0, 2, 0, 2)
        };
        for (int m = 0; m < 60; m++) _scheduleMinute.Items.Add(m.ToString("00"));
        _scheduleMinute.SelectedIndexChanged += (_, _) => UpdateScheduleStatus();

        var colon = new Label
        {
            Text = ":", AutoSize = true,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Margin = new Padding(0, 6, 6, 0)
        };

        timePanel.Controls.Add(_scheduleHour);
        timePanel.Controls.Add(colon);
        timePanel.Controls.Add(_scheduleMinute);
        fireGrid.Controls.Add(timePanel, 1, 1);

        // Clear is a text link (underlined blue) — cleaner than a button,
        // also dodges the spacing issue from the previous Button-in-fixed-
        // -column layout.
        var clearLink = new LinkLabel
        {
            Text = "Clear",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 12, 0, 0),
            LinkBehavior = LinkBehavior.AlwaysUnderline
        };
        clearLink.LinkClicked += (_, _) =>
        {
            _scheduleHour.SelectedIndex = -1;
            _scheduleMinute.SelectedIndex = -1;
            UpdateScheduleStatus();
        };
        fireGrid.Controls.Add(clearLink, 2, 1);

        // No "gray" tag here — UpdateScheduleStatus drives ForeColor dynamically
        // (gray for normal states, red for the "please pick a valid time" hint).
        // Tagging it "gray" would let the theme Recolor overwrite our red on
        // every Shown / theme change.
        _scheduleStatus = new Label
        {
            AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.None,
            Margin = new Padding(8, 12, 0, 0),
            Tag = "status"
        };
        fireGrid.Controls.Add(_scheduleStatus, 3, 1);

        root.Controls.Add(fireGrid);

        // ---- After-fire behavior ----
        root.Controls.Add(MakeSectionLabel("After the fire"));

        _autoExitStay = new RadioButton
        {
            Text = "Stay in simulated sleep — the screen stays black until I dismiss it.",
            AutoSize = true, Margin = new Padding(0, 6, 0, 4)
        };
        _autoExitYes = new RadioButton
        {
            Text = "Exit simulated sleep and allow the device's real Sleep / Screen-off timeouts to take over.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 16)
        };
        root.Controls.Add(_autoExitStay);
        root.Controls.Add(_autoExitYes);

        // ---- Schedule-toggle hotkey (lives here so it's one feature) ----
        root.Controls.Add(MakeSectionLabel("Toggle hotkey"));
        root.Controls.Add(new Label
        {
            Text = "Press this hotkey to enter simulated sleep and activate the schedule above. "
                 + "Press it again to exit. (Click or any key while overlaid also exits.)",
            AutoSize = true, MaximumSize = new Size(820, 0),
            ForeColor = SystemColors.GrayText, Tag = "gray",
            Margin = new Padding(0, 0, 0, 8)
        });

        var hkGrid = MakeHotkeyGrid();
        var keyChoices = BuildKeyChoices();
        // Just one row, no section header needed
        AddHotkeyRow(hkGrid, 1, keyChoices, "schedule-toggle", "Schedule toggle");
        _scheduleHotkeyRow = _rows[_rows.Count - 1];

        root.Controls.Add(hkGrid);

        // Wrap root in a scrollable Panel so smaller windows don't crop.
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(root);
        tab.Controls.Add(scroll);

        // ---- Populate from model ----
        // Variant B has its own two-entry dropdown: index 0 = "(none)",
        // index 1 = "oneshot". Variant A keeps the v1.2.x four-entry
        // mapping ((none) / Adaptive / 80% / 100%).
        if (_variant == SurfaceUiVariant.B)
        {
            _scheduleMode.SelectedIndex = _model.ScheduleMode == "oneshot" ? 1 : 0;
        }
        else
        {
            switch (_model.ScheduleMode)
            {
                case "adaptive": _scheduleMode.SelectedIndex = 1; break;
                case "80":       _scheduleMode.SelectedIndex = 2; break;
                case "100":      _scheduleMode.SelectedIndex = 3; break;
                default:         _scheduleMode.SelectedIndex = 0; break;
            }
        }
        _scheduleDuration.SelectedIndex = _model.ScheduleDuration == "1week" ? 1 : 0;

        // Populate hour/minute dropdowns from saved "HH:MM" string.
        if (!string.IsNullOrEmpty(_model.ScheduleTime)
            && TryParseTime(_model.ScheduleTime!, out int hh, out int mm))
        {
            _scheduleHour.SelectedIndex   = hh;
            _scheduleMinute.SelectedIndex = mm;
        }
        else
        {
            _scheduleHour.SelectedIndex   = -1;
            _scheduleMinute.SelectedIndex = -1;
        }

        _autoExitYes.Checked  = _model.ScheduleAutoExit;
        _autoExitStay.Checked = !_model.ScheduleAutoExit;
        UpdateDurationVisibility();
        UpdateScheduleStatus();
    }

    private void UpdateDurationVisibility()
    {
        // Duration is variant-A-only: the 1-day vs 1-week distinction lives
        // in the Surface app's radio UI alongside the 100% mode. Variant B
        // has no equivalent concept (one-shot is fire-and-forget for one
        // charge cycle), so the duration row is always hidden there.
        bool is100 = _variant == SurfaceUiVariant.A && _scheduleMode.SelectedIndex == 3;
        _scheduleDurationLabel.Visible = is100;
        _scheduleDuration.Visible      = is100;
    }

    private void UpdateScheduleStatus()
    {
        int hh = _scheduleHour.SelectedIndex;
        int mm = _scheduleMinute.SelectedIndex;

        bool dark = DarkMode.IsAppsDarkMode();
        Color grayFg  = dark ? Color.FromArgb(0xBB, 0xBB, 0xBB) : SystemColors.GrayText;
        // Dark mode uses a softer red so it doesn't strain against the dark bg.
        Color errorFg = dark ? Color.FromArgb(0xF0, 0x70, 0x70) : Color.FromArgb(0xC0, 0x2A, 0x2A);

        if (hh < 0 && mm < 0)
        {
            _scheduleStatus.Text      = "(not set — hotkey enters simulated sleep without a fire)";
            _scheduleStatus.ForeColor = grayFg;
            if (_btnSave != null) _btnSave.Enabled = true;
        }
        else if (hh < 0 || mm < 0)
        {
            _scheduleStatus.Text      = "Please pick a valid time.";
            _scheduleStatus.ForeColor = errorFg;
            // Disable Save so the invalid state can't be committed — no dialog,
            // user just can't proceed until they fix the field.
            if (_btnSave != null) _btnSave.Enabled = false;
        }
        else
        {
            _scheduleStatus.Text      = $"will fire at {hh:00}:{mm:00}";
            _scheduleStatus.ForeColor = grayFg;
            if (_btnSave != null) _btnSave.Enabled = true;
        }
    }

    private static bool TryParseTime(string s, out int hh, out int mm)
    {
        hh = mm = 0;
        var colon = s.IndexOf(':');
        if (colon <= 0 || colon >= s.Length - 1) return false;
        if (!int.TryParse(s[..colon], out hh)) return false;
        if (!int.TryParse(s[(colon + 1)..], out mm)) return false;
        return hh >= 0 && hh <= 23 && mm >= 0 && mm <= 59;
    }

    private Label MakeSectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        Margin = new Padding(0, 4, 0, 6),
        Anchor = AnchorStyles.Left,
        Tag = "section"
    };

    private static Label MakeFieldLabel(string text) => new()
    {
        Text = text, AutoSize = true,
        Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 8, 0)
    };

    // ---- Shared hotkey row helpers (used by both tabs) -----------------

    private static int AddSectionHeader(TableLayoutPanel grid, int rowIdx, string text)
    {
        var lbl = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Margin = new Padding(0, rowIdx == 1 ? 4 : 18, 0, 6),
            Anchor = AnchorStyles.Left,
            Tag = "section"
        };
        grid.Controls.Add(lbl, 0, rowIdx);
        grid.SetColumnSpan(lbl, 6);
        return rowIdx + 1;
    }

    private int AddHotkeyRow(TableLayoutPanel grid, int rowIdx, string[] keyChoices, string action, string label)
    {
        var row = new Row { Action = action };
        row.Enable = new CheckBox { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 0, 9) };
        row.Win   = MakeModCheckbox();
        row.Alt   = MakeModCheckbox();
        row.Ctrl  = MakeModCheckbox();
        row.Shift = MakeModCheckbox();
        row.Key = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 130, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 0, 7)
        };
        row.Key.Items.AddRange(keyChoices);
        if (_model.Hotkeys.TryGetValue(action, out var h))
        {
            row.Enable.Checked = h.Enabled;
            ParseHotkeyToControls(h.Key, row);
        }
        grid.Controls.Add(row.Enable, 0, rowIdx);
        grid.Controls.Add(row.Win,    1, rowIdx);
        grid.Controls.Add(row.Alt,    2, rowIdx);
        grid.Controls.Add(row.Ctrl,   3, rowIdx);
        grid.Controls.Add(row.Shift,  4, rowIdx);
        grid.Controls.Add(row.Key,    5, rowIdx);
        _rows.Add(row);
        return rowIdx + 1;
    }

    private static Label MakeHeaderLabel(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
        ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6),
        Tag = "gray"
    };

    private static CheckBox MakeModCheckbox() => new()
    {
        AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 0, 9)
    };

    private static string[] BuildKeyChoices()
    {
        var keys = new List<string>();
        for (int i = 0; i <= 9; i++) keys.Add(i.ToString());
        for (char c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
        for (int f = 1; f <= 12; f++) keys.Add($"F{f}");
        keys.AddRange(new[] { "Space", "Enter", "Tab", "Escape" });
        return keys.ToArray();
    }

    private static void ParseHotkeyToControls(string s, Row r)
    {
        if (string.IsNullOrEmpty(s)) return;
        int i = 0;
        while (i < s.Length)
        {
            switch (s[i])
            {
                case '#': r.Win.Checked   = true; i++; continue;
                case '!': r.Alt.Checked   = true; i++; continue;
                case '^': r.Ctrl.Checked  = true; i++; continue;
                case '+': r.Shift.Checked = true; i++; continue;
            }
            break;
        }
        var key = s[i..].Trim();
        if (key.Length == 0) return;
        string display = key.Length == 1 ? char.ToUpperInvariant(key[0]).ToString() : key.ToUpperInvariant();
        if (r.Key.Items.Contains(display)) r.Key.SelectedItem = display;
    }

    // ---- Save ----------------------------------------------------------

    private void SaveAndClose()
    {
        // Defensive: should be unreachable — Save button is disabled when
        // exactly one of hour/minute is set. No dialog (inline status handles
        // notification); silently bail so we never persist a half-set time.
        int hh = _scheduleHour.SelectedIndex;
        int mm = _scheduleMinute.SelectedIndex;
        if ((hh < 0) != (mm < 0)) return;
        string? timeText = (hh >= 0 && mm >= 0) ? $"{hh:00}:{mm:00}" : null;

        // Hotkey rows (both tabs)
        foreach (var r in _rows)
        {
            var sb = new StringBuilder();
            if (r.Win.Checked)   sb.Append('#');
            if (r.Alt.Checked)   sb.Append('!');
            if (r.Ctrl.Checked)  sb.Append('^');
            if (r.Shift.Checked) sb.Append('+');
            sb.Append(r.Key.SelectedItem ?? "");
            if (_model.Hotkeys.TryGetValue(r.Action, out var h))
            {
                h.Enabled = r.Enable.Checked;
                h.Key     = sb.ToString();
            }
        }

        // Schedule fields — variant-aware. Variant B's dropdown has only
        // two entries ((none) / oneshot); we serialize that as
        // ScheduleMode='oneshot' (no Duration). Variant A keeps the
        // v1.2.x mapping.
        if (_variant == SurfaceUiVariant.B)
        {
            _model.ScheduleMode     = _scheduleMode.SelectedIndex == 1 ? "oneshot" : null;
            _model.ScheduleDuration = null;
        }
        else
        {
            switch (_scheduleMode.SelectedIndex)
            {
                case 1: _model.ScheduleMode = "adaptive"; _model.ScheduleDuration = null; break;
                case 2: _model.ScheduleMode = "80";       _model.ScheduleDuration = null; break;
                case 3:
                    _model.ScheduleMode = "100";
                    _model.ScheduleDuration = _scheduleDuration.SelectedIndex == 1 ? "1week" : "1day";
                    break;
                default:
                    _model.ScheduleMode = null;
                    _model.ScheduleDuration = null;
                    break;
            }
        }
        _model.ScheduleTime     = timeText;
        _model.ScheduleAutoExit = _autoExitYes.Checked;

        // "Run at Windows login" — apply the registry change directly.
        // (AutoStart state is read from / written to the user's Run key, not
        // settings.ini, so it isn't part of _model.)
        try
        {
            bool installed = AutoStart.IsInstalled();
            if (_autoStartCheck.Checked && !installed)      AutoStart.Install();
            else if (!_autoStartCheck.Checked && installed) AutoStart.Uninstall();
        }
        catch
        {
            // Don't block Save on an AutoStart registry hiccup — settings
            // file is the authoritative one. Silent failure is acceptable
            // here; the checkbox will reflect the actual state next time.
        }

        _model.Save();
        Saved?.Invoke();
        Close();
    }

    // ---- Variant banner + Re-detect -----------------------------------

    private string BuildVariantBannerText()
    {
        var version = _model.DetectedAtAppVersion ?? "(not yet detected)";
        return _variant switch
        {
            SurfaceUiVariant.A =>
                $"Detected device variant: A — three charging modes available (Adaptive / 80% / 100%).  "
                + $"Last checked at app v{version}.",
            SurfaceUiVariant.B =>
                $"Detected device variant: B — your Surface app exposes only the 'Charge to 100%' "
                + $"one-shot override. Other modes aren't available on this device.  "
                + $"Last checked at app v{version}.",
            _ =>
                "Device variant: not yet detected. Click 'Refresh status' in the tray menu, or run "
                + "the diagnostic tool if detection keeps failing."
        };
    }

    private void RunRedetect()
    {
        _btnRedetect.Enabled = false;
        _variantBanner.Text = "Re-detecting device... (this opens the Surface app briefly)";

        // Run RefreshState on a thread-pool thread so the dialog stays
        // responsive. RefreshState's internal DetectVariant writes the
        // result to settings.ini; we reload the model on completion to
        // pick up the new value, then update the banner.
        Task.Run(() =>
        {
            string? err = null;
            try { err = SurfaceController.RefreshState(); }
            catch (Exception ex) { err = ex.Message; }

            BeginInvoke(() =>
            {
                // Reload from disk to pick up the new DetectedVariant +
                // DetectedAtAppVersion the silent detection wrote.
                var fresh = SettingsModel.Load();
                _model.DetectedVariant      = fresh.DetectedVariant;
                _model.DetectedAtAppVersion = fresh.DetectedAtAppVersion;
                _model.OneShotButtonId      = fresh.OneShotButtonId;
                _model.OneShotButtonName    = fresh.OneShotButtonName;

                var newVariant = ParseVariantString(_model.DetectedVariant);
                bool variantChanged = newVariant != _variant;
                _variant = newVariant;

                _variantBanner.Text = BuildVariantBannerText();
                if (variantChanged)
                {
                    _variantBanner.Text += "  Save and reopen this dialog to refresh the hotkey rows.";
                }
                if (err != null)
                {
                    _variantBanner.Text += $"  (Refresh reported: {err})";
                }
                _btnRedetect.Enabled = true;
            });
        });
    }

    // ---- Theming -------------------------------------------------------

    private void ApplyTheme()
    {
        if (!DarkMode.IsAppsDarkMode()) return;
        var bg     = Color.FromArgb(0x1F, 0x1F, 0x1F);
        var fg     = Color.White;
        var grayFg = Color.FromArgb(0xBB, 0xBB, 0xBB);
        var btnBg  = Color.FromArgb(0x33, 0x33, 0x33);
        var border = Color.FromArgb(0x55, 0x55, 0x55);
        BackColor = bg; ForeColor = fg;
        Recolor(this, bg, fg, grayFg, btnBg, border);
    }

    private static void Recolor(Control parent, Color bg, Color fg, Color grayFg, Color btnBg, Color border)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case LinkLabel link:
                    link.LinkColor        = Color.FromArgb(0x4D, 0xA6, 0xFF);
                    link.ActiveLinkColor  = Color.FromArgb(0x80, 0xC0, 0xFF);
                    link.VisitedLinkColor = link.LinkColor;
                    link.ForeColor        = grayFg;
                    link.BackColor        = bg;
                    break;
                case Label lbl:
                    // "status" labels manage their own ForeColor dynamically
                    // (red for errors, gray otherwise) — don't overwrite it.
                    if ((lbl.Tag as string) != "status")
                        lbl.ForeColor = (lbl.Tag as string) == "gray" ? grayFg : fg;
                    lbl.BackColor = bg;
                    break;
                case CheckBox cb:
                    cb.ForeColor = fg;
                    cb.BackColor = bg;
                    cb.FlatStyle = FlatStyle.Standard;
                    // Native dark theme for the check box glyph itself.
                    // Without this the small square sits as light system
                    // chrome inside the otherwise-dark control.
                    DarkMode.ApplyDarkExplorerTheme(cb);
                    break;
                case RadioButton rb:
                    rb.ForeColor = fg;
                    rb.BackColor = bg;
                    rb.FlatStyle = FlatStyle.Standard;
                    DarkMode.ApplyDarkExplorerTheme(rb);
                    break;
                case Button btn:
                    btn.ForeColor = fg;
                    btn.BackColor = btnBg;
                    btn.FlatStyle = FlatStyle.Flat;
                    btn.FlatAppearance.BorderColor = border;
                    break;
                case ComboBox combo:
                    combo.ForeColor = fg;
                    combo.BackColor = Color.FromArgb(0x2B, 0x2B, 0x2B);
                    // NOTE: DO NOT set FlatStyle = Flat here. FlatStyle.Flat
                    // makes WinForms paint the combo's chrome itself, which
                    // OVERRIDES the native DarkMode_CFD theme — the dropdown
                    // arrow stays system-light. Standard FlatStyle + native
                    // dark theme gives a fully dark control including the
                    // arrow button.
                    combo.FlatStyle = FlatStyle.Standard;
                    DarkMode.ApplyDarkComboBoxTheme(combo);
                    DarkMode.HookComboBoxDropdownDarkTheme(combo);
                    break;
                case TextBox tb:
                    tb.ForeColor = fg;
                    tb.BackColor = Color.FromArgb(0x2B, 0x2B, 0x2B);
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case DarkTabControl dtc:
                    // Subclass handles its own painting via WndProc — just
                    // propagate the current scheme colors in case ApplyTheme
                    // is called again with different values.
                    dtc.DarkBackground     = bg;
                    dtc.DarkTabFg          = fg;
                    dtc.DarkTabSelectedBg  = btnBg;
                    dtc.DarkBorder         = border;
                    dtc.Invalidate();
                    break;
                case TabControl tc:
                    // Fallback: plain TabControl (used only when dark mode
                    // is off at construction time). Leave as-is.
                    tc.BackColor = bg;
                    tc.ForeColor = fg;
                    break;
                case TabPage tp:
                    tp.BackColor = bg;
                    tp.ForeColor = fg;
                    break;
                case TableLayoutPanel _:
                case FlowLayoutPanel _:
                case Panel _:
                    c.BackColor = bg;
                    c.ForeColor = fg;
                    // If this panel scrolls (AutoScroll = true), native dark
                    // theme makes its scrollbars dark too. Safe to call on
                    // non-scrolling panels — no visual effect.
                    DarkMode.ApplyDarkExplorerTheme(c);
                    break;
            }
            Recolor(c, bg, fg, grayFg, btnBg, border);
        }
    }

}
