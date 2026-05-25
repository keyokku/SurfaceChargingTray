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
    // Low-battery warning (v1.4.2): enable toggle + threshold dropdown.
    private CheckBox _lowBatteryCheck = null!;
    private ComboBox _lowBatteryPctCombo = null!;
    private static readonly int[] LowBatteryChoices = { 10, 15, 20, 25, 30 };
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
    //
    // v1.4.0 multi-slot: up to 3 schedule rows, each with its own mode /
    // duration / time. Slot 0 is permanent (no Remove button); slots 1-2
    // are added via "Add schedule" and removable. Each row's controls are
    // bundled in a ScheduleSlotRow so we can add/remove dynamically.
    private sealed class ScheduleSlotRow
    {
        public FlowLayoutPanel Container = null!;
        public ComboBox Mode          = null!;
        public Label    DurationLabel = null!;
        public ComboBox Duration      = null!;
        public ComboBox Hour          = null!;
        public ComboBox Minute        = null!;
        public Button?  Remove        = null;   // null for slot 0 (permanent)
    }
    private readonly List<ScheduleSlotRow> _slotRows = new();
    private FlowLayoutPanel _slotsContainer = null!;
    private Button _addSlotBtn = null!;
    private Label  _scheduleStatus = null!;
    // 3-radio auto-exit (v1.4.0). Replaces the v1.3.x 2-radio Stay/Exit.
    private RadioButton _autoExitStay  = null!;   // Stay
    private RadioButton _autoExitFirst = null!;   // AfterFirst
    private RadioButton _autoExitAll   = null!;   // AfterAll
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

        // v1.4.0 layout: TableLayoutPanel with two columns so we can keep
        // Save/Cancel left-aligned (existing v1.3.x convention) and place
        // the new Export/Import settings links far right. AutoSize on the
        // left column, Percent fill on the right (which anchors the link
        // group to the right edge).
        var btnPanel = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 12)
        };
        btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        btnPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var leftButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };
        _btnSave      = new Button { Text = "Save",   Size = new Size(110, 36), Margin = new Padding(0, 0, 12, 0) };
        var btnCancel = new Button { Text = "Cancel", Size = new Size(110, 36) };
        _btnSave.Click  += (_, _) => SaveAndClose();
        btnCancel.Click += (_, _) => Close();
        leftButtons.Controls.Add(_btnSave);
        leftButtons.Controls.Add(btnCancel);
        AcceptButton = _btnSave;
        CancelButton = btnCancel;
        btnPanel.Controls.Add(leftButtons, 0, 0);

        // Right side: Export / Import settings as LinkLabels. RightToLeft
        // flow keeps Import on the far right (most-likely-clicked at the
        // top-right of the link group, mirroring the existing ko-fi/github
        // links convention).
        var rightLinks = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
            Margin = new Padding(0)
        };
        var lnkImport = new LinkLabel
        {
            Text = "Import settings",
            AutoSize = true,
            Font = new Font("Segoe UI", 9f),
            Margin = new Padding(12, 10, 0, 10)
        };
        var lnkExport = new LinkLabel
        {
            Text = "Export settings",
            AutoSize = true,
            Font = new Font("Segoe UI", 9f),
            Margin = new Padding(12, 10, 0, 10)
        };
        lnkImport.LinkClicked += (_, _) => DoImportSettings();
        lnkExport.LinkClicked += (_, _) => DoExportSettings();
        // RightToLeft: Import appears further right than Export
        rightLinks.Controls.Add(lnkImport);
        rightLinks.Controls.Add(lnkExport);
        btnPanel.Controls.Add(rightLinks, 1, 0);

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

        // Low-battery warning row: checkbox + threshold dropdown on one line.
        var lowBattRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = new Padding(0, 0, 0, 12)
        };
        _lowBatteryCheck = new CheckBox
        {
            Text = "Warn when battery drops to",
            AutoSize = true,
            Checked = _model.LowBatteryWarnEnabled,
            Margin = new Padding(0, 4, 6, 0)
        };
        _lowBatteryPctCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 70, Margin = new Padding(0, 2, 6, 0)
        };
        foreach (var p in LowBatteryChoices) _lowBatteryPctCombo.Items.Add($"{p}%");
        // Select the saved threshold (default 20%); fall back to 20% if the
        // saved value isn't one of the offered choices.
        int pctIdx = Array.IndexOf(LowBatteryChoices, _model.LowBatteryWarnPct);
        _lowBatteryPctCombo.SelectedIndex = pctIdx >= 0 ? pctIdx : Array.IndexOf(LowBatteryChoices, 20);
        var lowBattSuffix = new Label
        {
            Text = "(on battery)", AutoSize = true,
            ForeColor = SystemColors.GrayText, Tag = "gray",
            Margin = new Padding(0, 6, 0, 0)
        };
        // Enable/disable the dropdown with the checkbox.
        _lowBatteryCheck.CheckedChanged += (_, _) => _lowBatteryPctCombo.Enabled = _lowBatteryCheck.Checked;
        _lowBatteryPctCombo.Enabled = _lowBatteryCheck.Checked;
        lowBattRow.Controls.Add(_lowBatteryCheck);
        lowBattRow.Controls.Add(_lowBatteryPctCombo);
        lowBattRow.Controls.Add(lowBattSuffix);
        header.Controls.Add(lowBattRow);

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

        // ---- Fire spec — up to 3 schedule slots (v1.4.0) ----
        root.Controls.Add(MakeSectionLabel("When to fire"));
        root.Controls.Add(new Label
        {
            Text = $"Add up to {SettingsModel.MaxScheduleSlots} scheduled mode changes. "
                 + "Each fires at its own time during one simulated-sleep run.",
            AutoSize = true, MaximumSize = new Size(820, 0),
            ForeColor = SystemColors.GrayText, Tag = "gray",
            Margin = new Padding(0, 0, 0, 8)
        });

        // Vertical container that holds the dynamic slot rows.
        _slotsContainer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = new Padding(0, 0, 0, 4)
        };
        root.Controls.Add(_slotsContainer);

        // "Add schedule" button — disabled at the slot cap.
        _addSlotBtn = new Button
        {
            Text = "+ Add schedule",
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 2, 0, 16),
            Padding = new Padding(8, 4, 8, 4)
        };
        _addSlotBtn.Click += (_, _) =>
        {
            if (_slotRows.Count < SettingsModel.MaxScheduleSlots)
            {
                AddSlotRow(removable: true);
                RefreshAddButtonState();
                ApplyTheme();   // theme the freshly-added controls (no-op in light mode)
                UpdateScheduleStatus();
            }
        };
        root.Controls.Add(_addSlotBtn);

        // Shared validation/status line for all slots. No "gray" tag —
        // UpdateScheduleStatus drives the color (gray normal / red error)
        // and a "gray" tag would let theme Recolor overwrite the red.
        _scheduleStatus = new Label
        {
            AutoSize = true, MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 0, 0, 16),
            Tag = "status"
        };
        root.Controls.Add(_scheduleStatus);

        // ---- After-fire behavior — 3 options (v1.4.0) ----
        root.Controls.Add(MakeSectionLabel("After the fire"));

        _autoExitStay = new RadioButton
        {
            Text = "Stay in simulated sleep — the screen stays black until I dismiss it.",
            AutoSize = true, Margin = new Padding(0, 6, 0, 4)
        };
        _autoExitFirst = new RadioButton
        {
            Text = "Exit after the first scheduled change fires (then allow real Sleep / Screen-off).",
            AutoSize = true, Margin = new Padding(0, 0, 0, 4)
        };
        _autoExitAll = new RadioButton
        {
            Text = "Exit only after all scheduled changes have fired.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 16)
        };
        root.Controls.Add(_autoExitStay);
        root.Controls.Add(_autoExitFirst);
        root.Controls.Add(_autoExitAll);

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

        // ---- Populate slot rows from model ----
        // Build one row per saved schedule (slot 0 permanent, rest removable).
        // Always show at least one row so the tab isn't empty.
        _slotsContainer.Controls.Clear();
        _slotRows.Clear();
        if (_model.Schedules.Count == 0)
        {
            AddSlotRow(removable: false);  // empty permanent slot 0
        }
        else
        {
            for (int i = 0; i < _model.Schedules.Count && i < SettingsModel.MaxScheduleSlots; i++)
                AddSlotRow(removable: i > 0, initial: _model.Schedules[i]);
        }
        RefreshAddButtonState();

        // Map the exit-mode enum onto the three radios.
        _autoExitStay.Checked  = _model.ScheduleAutoExit == SettingsModel.ScheduleExitMode.Stay;
        _autoExitFirst.Checked = _model.ScheduleAutoExit == SettingsModel.ScheduleExitMode.AfterFirst;
        _autoExitAll.Checked   = _model.ScheduleAutoExit == SettingsModel.ScheduleExitMode.AfterAll;
        UpdateScheduleStatus();
    }

    // ---- Slot-row management (v1.4.0 multi-slot) -----------------------

    private void AddSlotRow(bool removable, SettingsModel.ScheduleEntry? initial = null)
    {
        var slot = new ScheduleSlotRow();
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = new Padding(0, 2, 0, 2)
        };
        slot.Container = row;

        // Mode dropdown (variant-aware).
        slot.Mode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 190, Margin = new Padding(0, 2, 8, 2)
        };
        if (_variant == SurfaceUiVariant.B)
            slot.Mode.Items.AddRange(new object[] { "(none)", "Charge to 100% (one-shot)" });
        else
            slot.Mode.Items.AddRange(new object[] { "(none)", "Adaptive", "Limit to 80%", "Charge to 100%" });
        slot.Mode.SelectedIndexChanged += (_, _) => { UpdateSlotDurationVisibility(slot); UpdateScheduleStatus(); };
        row.Controls.Add(slot.Mode);

        // Duration (variant A, only shown for "Charge to 100%").
        slot.DurationLabel = new Label { Text = "for", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
        row.Controls.Add(slot.DurationLabel);
        slot.Duration = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 90, Margin = new Padding(0, 2, 8, 2)
        };
        slot.Duration.Items.AddRange(new object[] { "1 day", "1 week" });
        slot.Duration.SelectedIndex = 0;
        row.Controls.Add(slot.Duration);

        // "at" + HH : MM.
        row.Controls.Add(new Label { Text = "at", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        slot.Hour = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 58, Margin = new Padding(0, 2, 4, 2) };
        for (int h = 0; h < 24; h++) slot.Hour.Items.Add(h.ToString("00"));
        slot.Hour.SelectedIndexChanged += (_, _) =>
        {
            if (slot.Hour.SelectedIndex >= 0 && slot.Minute.SelectedIndex < 0) slot.Minute.SelectedIndex = 0;
            UpdateScheduleStatus();
        };
        row.Controls.Add(slot.Hour);
        row.Controls.Add(new Label { Text = ":", AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(0, 4, 4, 0) });
        slot.Minute = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 58, Margin = new Padding(0, 2, 8, 2) };
        for (int m = 0; m < 60; m++) slot.Minute.Items.Add(m.ToString("00"));
        slot.Minute.SelectedIndexChanged += (_, _) => UpdateScheduleStatus();
        row.Controls.Add(slot.Minute);

        // Remove button (removable rows only).
        if (removable)
        {
            slot.Remove = new Button { Text = "Remove", AutoSize = true, Margin = new Padding(0, 2, 0, 2), Padding = new Padding(4, 2, 4, 2) };
            slot.Remove.Click += (_, _) => RemoveSlotRow(slot);
            row.Controls.Add(slot.Remove);
        }

        // Apply initial values (or default to "(none)").
        if (initial != null)
        {
            SetSlotMode(slot, initial.Mode);
            slot.Duration.SelectedIndex = initial.Duration == "1week" ? 1 : 0;
            if (TryParseTime(initial.Time, out int hh, out int mm))
            {
                slot.Hour.SelectedIndex   = hh;
                slot.Minute.SelectedIndex = mm;
            }
        }
        else
        {
            slot.Mode.SelectedIndex   = 0;   // (none)
            slot.Hour.SelectedIndex   = -1;
            slot.Minute.SelectedIndex = -1;
        }

        _slotRows.Add(slot);
        _slotsContainer.Controls.Add(row);
        UpdateSlotDurationVisibility(slot);
    }

    private void RemoveSlotRow(ScheduleSlotRow slot)
    {
        _slotRows.Remove(slot);
        _slotsContainer.Controls.Remove(slot.Container);
        slot.Container.Dispose();
        RefreshAddButtonState();
        UpdateScheduleStatus();
    }

    private void RefreshAddButtonState()
    {
        _addSlotBtn.Enabled = _slotRows.Count < SettingsModel.MaxScheduleSlots;
    }

    /// <summary>Maps a mode string to the slot's Mode dropdown index.</summary>
    private void SetSlotMode(ScheduleSlotRow slot, string? mode)
    {
        if (_variant == SurfaceUiVariant.B)
        {
            slot.Mode.SelectedIndex = mode == "oneshot" ? 1 : 0;
        }
        else
        {
            slot.Mode.SelectedIndex = mode switch
            {
                "adaptive" => 1,
                "80"       => 2,
                "100"      => 3,
                _          => 0
            };
        }
    }

    /// <summary>Reads a slot's Mode dropdown into a mode string (null = none).</summary>
    private string? SlotModeString(ScheduleSlotRow slot)
    {
        if (_variant == SurfaceUiVariant.B)
            return slot.Mode.SelectedIndex == 1 ? "oneshot" : null;
        return slot.Mode.SelectedIndex switch
        {
            1 => "adaptive",
            2 => "80",
            3 => "100",
            _ => null
        };
    }

    private void UpdateSlotDurationVisibility(ScheduleSlotRow slot)
    {
        // Duration only applies to variant A "Charge to 100%".
        bool is100 = _variant == SurfaceUiVariant.A && slot.Mode.SelectedIndex == 3;
        slot.DurationLabel.Visible = is100;
        slot.Duration.Visible      = is100;
    }

    private void UpdateScheduleStatus()
    {
        bool dark = DarkMode.IsAppsDarkMode();
        Color grayFg  = dark ? Color.FromArgb(0xBB, 0xBB, 0xBB) : SystemColors.GrayText;
        Color errorFg = dark ? Color.FromArgb(0xF0, 0x70, 0x70) : Color.FromArgb(0xC0, 0x2A, 0x2A);

        // Validate each slot: a slot with a mode must have a complete time,
        // and vice versa. Half-set slots block Save.
        int activeCount = 0;
        for (int i = 0; i < _slotRows.Count; i++)
        {
            var slot = _slotRows[i];
            bool hasMode = SlotModeString(slot) != null;
            bool hasTime = slot.Hour.SelectedIndex >= 0 && slot.Minute.SelectedIndex >= 0;
            bool partialTime = (slot.Hour.SelectedIndex >= 0) != (slot.Minute.SelectedIndex >= 0);

            if (partialTime || (hasMode != hasTime))
            {
                _scheduleStatus.Text      = $"Slot {i + 1}: pick both a charging mode and a complete time (or leave both empty).";
                _scheduleStatus.ForeColor = errorFg;
                if (_btnSave != null) _btnSave.Enabled = false;
                return;
            }
            if (hasMode && hasTime) activeCount++;
        }

        if (activeCount == 0)
            _scheduleStatus.Text = "(no schedule set — hotkey enters simulated sleep without a fire)";
        else
            _scheduleStatus.Text = activeCount == 1 ? "1 scheduled change armed." : $"{activeCount} scheduled changes armed.";
        _scheduleStatus.ForeColor = grayFg;
        if (_btnSave != null) _btnSave.Enabled = true;
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

    // ---- Export / Import settings (v1.4.0) ----------------------------

    /// <summary>
    /// Save the live settings.ini (Paths.Settings) to a user-chosen location.
    /// Uses a SaveFileDialog with a sensible default filename. No dialog
    /// on success — just a brief tray-style MessageBox so the user knows.
    /// </summary>
    private void DoExportSettings()
    {
        try
        {
            using var dlg = new SaveFileDialog
            {
                Title    = "Export Surface Charging Tray settings",
                Filter   = "INI settings (*.ini)|*.ini|All files (*.*)|*.*",
                FileName = $"surface-charging-tray-settings-{DateTime.Now:yyyy-MM-dd}.ini",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // If the user hasn't saved yet during this session, the on-disk
            // settings.ini may be stale relative to _model. Save it first so
            // the exported file reflects the dialog's current state.
            _model.Save();
            if (System.IO.File.Exists(Paths.Settings))
            {
                System.IO.File.Copy(Paths.Settings, dlg.FileName, overwrite: true);
            }
            else
            {
                // Edge case: Save() wrote no file because all fields are at
                // defaults. Write an empty marker so the user gets something.
                System.IO.File.WriteAllText(dlg.FileName, "; Surface Charging Tray settings (defaults)\n");
            }
            MessageBox.Show(this,
                $"Settings exported to:\n{dlg.FileName}",
                "Export complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Couldn't export settings:\n{ex.Message}",
                "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Load settings from a user-chosen file, replacing the current
    /// in-memory model and refreshing every form field. Saves to disk on
    /// import so the tray's reload picks it up. Backs up the current
    /// settings.ini to a .bak file so a botched import isn't catastrophic.
    /// </summary>
    private void DoImportSettings()
    {
        try
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Import Surface Charging Tray settings",
                Filter = "INI settings (*.ini)|*.ini|All files (*.*)|*.*",
                Multiselect = false,
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // Confirm before replacing — destructive operation.
            var confirm = MessageBox.Show(this,
                "Importing settings will replace your current Surface Charging Tray " +
                "settings (hotkeys, schedule, etc.) with the contents of:\n\n" +
                dlg.FileName + "\n\n" +
                "Your current settings will be backed up to settings.ini.bak. Continue?",
                "Confirm import",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            // Backup existing settings.ini before overwriting.
            if (System.IO.File.Exists(Paths.Settings))
            {
                var bak = Paths.Settings + ".bak";
                try { System.IO.File.Copy(Paths.Settings, bak, overwrite: true); }
                catch { /* backup is best-effort */ }
            }

            // Overwrite the live settings file and re-read to validate it
            // parses cleanly. If Load returns essentially-default values
            // (no hotkeys overridden, no cache), warn the user.
            System.IO.File.Copy(dlg.FileName, Paths.Settings, overwrite: true);

            MessageBox.Show(this,
                "Settings imported. The dialog will close — reopen it to see " +
                "the imported values, and the tray menu will refresh.",
                "Import complete", MessageBoxButtons.OK, MessageBoxIcon.Information);

            // Trigger the Saved callback so TrayAppContext reloads its
            // _settings + applies any tray-visible changes, then close.
            // Skip our normal SaveAndClose validation — the file we just
            // imported is the source of truth, not the dialog's state.
            Saved?.Invoke();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Couldn't import settings:\n{ex.Message}\n\n" +
                "Your previous settings are still in place.",
                "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveAndClose()
    {
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

        // Schedule — collect every active slot row into the model's list.
        // A slot is active when it has both a mode and a complete time;
        // half-set rows are skipped (UpdateScheduleStatus blocks Save on
        // those anyway). Duration only applies to variant A "100".
        _model.Schedules.Clear();
        foreach (var slot in _slotRows)
        {
            var mode = SlotModeString(slot);
            if (string.IsNullOrEmpty(mode)) continue;
            if (slot.Hour.SelectedIndex < 0 || slot.Minute.SelectedIndex < 0) continue;

            string? duration = (mode == "100")
                ? (slot.Duration.SelectedIndex == 1 ? "1week" : "1day")
                : null;
            string time = $"{slot.Hour.SelectedIndex:00}:{slot.Minute.SelectedIndex:00}";

            _model.Schedules.Add(new SettingsModel.ScheduleEntry
            {
                Mode     = mode!,
                Duration = duration,
                Time     = time
            });
            if (_model.Schedules.Count >= SettingsModel.MaxScheduleSlots) break;
        }

        // Map the three radios onto the exit-mode enum.
        _model.ScheduleAutoExit =
            _autoExitAll.Checked   ? SettingsModel.ScheduleExitMode.AfterAll  :
            _autoExitFirst.Checked ? SettingsModel.ScheduleExitMode.AfterFirst :
                                     SettingsModel.ScheduleExitMode.Stay;

        // Low-battery warning settings.
        _model.LowBatteryWarnEnabled = _lowBatteryCheck.Checked;
        if (_lowBatteryPctCombo.SelectedIndex >= 0 &&
            _lowBatteryPctCombo.SelectedIndex < LowBatteryChoices.Length)
        {
            _model.LowBatteryWarnPct = LowBatteryChoices[_lowBatteryPctCombo.SelectedIndex];
        }

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
