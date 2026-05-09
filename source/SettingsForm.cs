using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace SurfaceChargingTray;

internal class SettingsForm : Form
{
    private readonly SettingsModel _model;
    public Action? Saved { get; set; }

    private class Row
    {
        public string Action = "";
        public CheckBox Enable = null!;
        public CheckBox Win = null!, Alt = null!, Ctrl = null!, Shift = null!;
        public ComboBox Key = null!;
    }

    private readonly List<Row> _rows = new();

    public SettingsForm(SettingsModel model)
    {
        _model = model;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = new Font("Segoe UI", 9.5f);
        BuildUI();
        HandleCreated += (_, _) => DarkMode.ApplyToForm(this);
        Shown += (_, _) => ApplyTheme();
    }

    private void BuildUI()
    {
        Text = "Surface Charging Tray — Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 660);
        MinimumSize = new Size(780, 540);
        Padding = new Padding(28);

        // ----- Bottom: footer (buttons + ko-fi link). Docks to bottom and
        //       AutoSizes to its content, so it always stays fully visible.
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
        var btnSave   = new Button { Text = "Save",   Size = new Size(110, 36), Margin = new Padding(0, 0, 12, 0) };
        var btnCancel = new Button { Text = "Cancel", Size = new Size(110, 36) };
        btnSave.Click   += (_, _) => SaveAndClose();
        btnCancel.Click += (_, _) => Close();
        btnPanel.Controls.Add(btnSave);
        btnPanel.Controls.Add(btnCancel);
        AcceptButton = btnSave;
        CancelButton = btnCancel;
        footer.Controls.Add(btnPanel);

        var link = new LinkLabel
        {
            Text = "If you liked this, consider a tip (ko-fi).",
            AutoSize = true,
            Font = new Font("Segoe UI", 8f),
            LinkArea = new LinkArea(30, 11),
            ForeColor = SystemColors.GrayText
        };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://ko-fi.com/keyokku") { UseShellExecute = true }); }
            catch { }
        };
        footer.Controls.Add(link);

        // ----- Top: header / tip text. Docks to top, autosizes height.
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 16)
        };
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

        // ----- Middle: the hotkey grid. Sits in a scrollable Panel so if
        //       the user shrinks the form below the grid's natural size,
        //       scrollbars appear instead of cropping.
        var grid = new TableLayoutPanel
        {
            ColumnCount = 6,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };
        // Action column wide enough for "Charge to 100% (1 week)";
        // modifier columns identical so the W/A/C/S labels align with their
        // checkboxes; key column wide enough for "Escape" / "Space".
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

        var actions = new (string Action, string Label)[]
        {
            ("adaptive",  "Adaptive"),
            ("80",        "Limit to 80%"),
            ("100-1day",  "Charge to 100% (1 day)"),
            ("100-1week", "Charge to 100% (1 week)"),
            ("cycle",     "Cycle through modes")
        };
        var keyChoices = BuildKeyChoices();

        int rowIdx = 1;
        foreach (var (action, label) in actions)
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
            rowIdx++;
        }

        var middle = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0)
        };
        middle.Controls.Add(grid);

        // Order matters: docked controls added BEFORE the Fill control so
        // the Fill claims only the remaining space. Top/bottom take what
        // they need first; middle takes whatever's left.
        Controls.Add(middle);  // added first so Fill sits "behind" docked
        Controls.Add(header);
        Controls.Add(footer);
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

    private void SaveAndClose()
    {
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
        _model.Save();
        Saved?.Invoke();
        Close();
    }

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
                    lbl.ForeColor = (lbl.Tag as string) == "gray" ? grayFg : fg;
                    lbl.BackColor = bg;
                    break;
                case CheckBox cb:
                    cb.ForeColor = fg;
                    cb.BackColor = bg;
                    cb.FlatStyle = FlatStyle.Standard;
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
                    combo.FlatStyle = FlatStyle.Flat;
                    break;
                case TableLayoutPanel _:
                case FlowLayoutPanel _:
                case Panel _:
                    c.BackColor = bg;
                    c.ForeColor = fg;
                    break;
            }
            Recolor(c, bg, fg, grayFg, btnBg, border);
        }
    }
}
