using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SurfaceChargingTray;

/// <summary>
/// Error display dialog. Shows the error text, a scrollable view of the
/// recent surface-error.log entries (v1.4.0+), and text-link actions for
/// copying the log + opening GitHub. Detection-failure errors get an
/// additional diagnostic-tool block with steps + a Download button.
///
/// Design (v1.4.0):
///   - Error message (red, wrapped)
///   - Detection-failure block (only when relevant): explanation + steps
///   - "Recent log entries:" label + read-only multi-line TextBox
///   - Text-link row: "Copy log to clipboard" | "Open GitHub issue"
///   - Detection-failure: "Download diagnostic tool" button
///   - Close button (bottom)
/// </summary>
internal static class ErrorDialog
{
    private const string ReleasesUrl =
        "https://github.com/keyokku/SurfaceChargingTray/releases/latest";
    private const string DetectionThreadUrl =
        "https://github.com/keyokku/SurfaceChargingTray/issues/2";
    // Issues list (not /issues/new) so users can search for existing
    // threads first instead of creating a duplicate report.
    private const string IssuesListUrl =
        "https://github.com/keyokku/SurfaceChargingTray/issues";

    public static void Show(IWin32Window? owner, string errorMessage)
    {
        bool isDetectionFailure =
            errorMessage.Contains("card not found", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("appear selected", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("radio button for mode", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("may not support", StringComparison.OrdinalIgnoreCase);

        using var dlg = Build(errorMessage, isDetectionFailure);
        if (owner != null) dlg.ShowDialog(owner);
        else dlg.ShowDialog();
    }

    private static Form Build(string errorMessage, bool showDiagnosticHelp)
    {
        var form = new Form
        {
            Text = "Surface Charging Tray — error",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Padding = new Padding(20),
            Font = new Font("Segoe UI", 9.5f),
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96f, 96f),
            // v1.4.0 bumped these to fit the log viewer + Copy/GitHub links
            // + Close button without scrolling the outer content area. The
            // detection-failure variant additionally has the steps block
            // and the Download diagnostic tool button, so it needs more
            // vertical room.
            ClientSize = showDiagnosticHelp ? new Size(720, 720) : new Size(720, 560),
            MinimumSize = showDiagnosticHelp ? new Size(640, 620) : new Size(620, 480)
        };

        // ---- Bottom-docked Close button row (added first so the Fill
        // content below claims only the remaining space)
        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,   // close button at far right
            Height = 56,
            Padding = new Padding(0, 12, 0, 0),
            AutoSize = false,
            WrapContents = false
        };
        var btnClose = MakeButton("Close", 90);
        btnClose.Click += (_, _) => form.Close();
        btnRow.Controls.Add(btnClose);

        // ---- Content stack (Fill)
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        int row = 0;

        // Error message (red, wrapped). Material Red 700 — readable on both
        // light and dark dialog backgrounds, distinctly red without going
        // orange/salmon like the previous lighter shade.
        var lblError = new Label
        {
            Text = errorMessage,
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            ForeColor = Color.FromArgb(0xD3, 0x2F, 0x2F),
            Margin = new Padding(0, 0, 0, 12)
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(lblError, 0, row++);

        // Diagnostic-help block — only for detection failures
        if (showDiagnosticHelp)
        {
            var lblExplain = new Label
            {
                Text = "Looks like the Surface app's UI on your device doesn't match what "
                     + "this tool expects. The diagnostic tool captures your Surface app's "
                     + "structure so I can support your model in a future update.",
                AutoSize = true,
                MaximumSize = new Size(660, 0),
                Margin = new Padding(0, 0, 0, 8)
            };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.Controls.Add(lblExplain, 0, row++);

            var lblStepsHeader = new Label
            {
                Text = "Steps:",
                AutoSize = true,
                Font = new Font(form.Font, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 4)
            };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.Controls.Add(lblStepsHeader, 0, row++);

            var lblSteps = new Label
            {
                Text = "  1. Download SurfaceChargingTrayDiagnostic.zip from Releases below.\n"
                     + "  2. Run the .exe matching your CPU (x64 or arm64).\n"
                     + "  3. Click 'Run Test' in the diagnostic dialog.\n"
                     + "  4. Post the .txt + .png it produces on the GitHub thread.",
                AutoSize = true,
                MaximumSize = new Size(660, 0),
                Margin = new Padding(0, 0, 0, 12)
            };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.Controls.Add(lblSteps, 0, row++);
        }

        // "Recent log entries" header
        var lblLogHeader = new Label
        {
            Text = "Recent log entries:",
            AutoSize = true,
            Font = new Font(form.Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(lblLogHeader, 0, row++);

        // Multi-line read-only TextBox with last N lines of the log.
        // ScrollBars.Both so users can scroll horizontally to see long
        // timestamps + message tails (lines often exceed 80 chars).
        // Anchored Left|Right so it stretches if the user resizes the dialog.
        var txtLog = new TextBox
        {
            Multiline   = true,
            ReadOnly    = true,
            ScrollBars  = ScrollBars.Both,
            WordWrap    = false,
            Width       = 660,
            Height      = showDiagnosticHelp ? 140 : 200,
            Anchor      = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            BorderStyle = BorderStyle.FixedSingle,
            Font        = new Font("Consolas", 8.75f),
            Text        = ReadRecentLogLines(50),
            Margin      = new Padding(0, 0, 0, 4)
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(txtLog, 0, row++);

        // Auto-scroll TextBox to bottom (most recent entries first visible)
        txtLog.HandleCreated += (_, _) =>
        {
            txtLog.SelectionStart  = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.ScrollToCaret();
        };

        // Text-link row: Copy log | Open GitHub issue
        var linkRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, showDiagnosticHelp ? 10 : 4),
            WrapContents = false
        };
        var lnkCopy = MakeLinkLabel("Copy log to clipboard");
        lnkCopy.LinkClicked += (_, _) =>
        {
            try { Clipboard.SetText(txtLog.Text ?? ""); } catch { }
        };
        var lnkIssue = MakeLinkLabel(
            showDiagnosticHelp ? "Open GitHub diagnostic thread" : "Open GitHub issues");
        lnkIssue.LinkClicked += (_, _) =>
            OpenUrl(showDiagnosticHelp ? DetectionThreadUrl : IssuesListUrl);
        linkRow.Controls.Add(lnkCopy);
        linkRow.Controls.Add(new Label { Width = 18, AutoSize = false });   // gap
        linkRow.Controls.Add(lnkIssue);
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(linkRow, 0, row++);

        // Detection-failure: Download diagnostic tool button
        if (showDiagnosticHelp)
        {
            var btnReleases = MakeButton("Download diagnostic tool", 220);
            btnReleases.Click += (_, _) => OpenUrl(ReleasesUrl);
            btnReleases.Margin = new Padding(0, 4, 0, 0);
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.Controls.Add(btnReleases, 0, row++);
        }

        // Order matters: btnRow added first (Bottom dock), content second (Fill).
        form.Controls.Add(content);
        form.Controls.Add(btnRow);
        form.AcceptButton = btnClose;
        form.CancelButton = btnClose;
        return form;
    }

    private static Button MakeButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 40,
        Margin = new Padding(0, 0, 8, 0)
    };

    private static LinkLabel MakeLinkLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        LinkBehavior = LinkBehavior.HoverUnderline,
        // Stronger default-link blue. #1F6FEB matches GitHub's link color
        // and reads cleanly on both light and dark dialog backgrounds.
        // Hover lightens (#3B82F6) for the typical link-hover feedback.
        LinkColor = Color.FromArgb(0x1F, 0x6F, 0xEB),
        ActiveLinkColor = Color.FromArgb(0x3B, 0x82, 0xF6),
        VisitedLinkColor = Color.FromArgb(0x1F, 0x6F, 0xEB),
        Margin = new Padding(0, 8, 0, 8)
    };

    /// <summary>
    /// Reads up to <paramref name="maxLines"/> trailing lines from the
    /// surface-error.log. Returns a friendly message if the log doesn't
    /// exist or is unreadable — the textbox is never empty/blank.
    /// </summary>
    private static string ReadRecentLogLines(int maxLines)
    {
        try
        {
            var path = Paths.ErrorLog;
            if (!File.Exists(path)) return "(no log entries yet)";
            // Read full file; surface-error.log is rotation-capped so it's
            // always small enough to slurp. Tail the last N lines.
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0) return "(log is empty)";
            int start = Math.Max(0, lines.Length - maxLines);
            return string.Join(Environment.NewLine, lines, start, lines.Length - start);
        }
        catch (Exception ex)
        {
            return $"(couldn't read log: {ex.Message})";
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
