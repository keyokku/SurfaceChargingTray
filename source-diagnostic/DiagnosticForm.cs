using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace SurfaceChargingTrayDiagnostic;

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class DiagnosticForm : Form
{
    // Centralized diagnostic-results thread that the developer maintains.
    // Users post their .txt + .png attachments as comments here rather than
    // each opening a separate new issue.
    private const string GitHubIssueUrl =
        "https://github.com/keyokku/SurfaceChargingTray/issues/2";

    private readonly Label _instructions;
    private readonly Label _status;
    private readonly Button _btnOpenSurface;
    private readonly Button _btnRunTest;
    private readonly Button _btnOpenFolder;
    private readonly Button _btnOpenGitHub;
    private readonly Button _btnClose;

    public DiagnosticForm()
    {
        Text = "Surface Charging Tray — Diagnostic Tool";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        // Per-monitor-V2 DPI mode is set in Program.cs; matching AutoScaleMode
        // here lets the dialog scale correctly on high-DPI Surface displays
        // (e.g. Pro 12 at 200%) instead of cramping into a tiny window.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(740, 540);
        MinimumSize = new Size(640, 460);
        Padding = new Padding(20);
        Font = new Font("Segoe UI", 9.5f);

        // ---- Header
        var title = new Label
        {
            Text = "Surface Charging Tray — Diagnostic Tool",
            AutoSize = true,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12)
        };

        // ---- Instructions block
        _instructions = new Label
        {
            Text = ComposeInstructionsText(),
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Margin = new Padding(0, 0, 0, 16)
        };

        // ---- Status line (updated during the run)
        _status = new Label
        {
            Text = "Status: Ready.",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 12)
        };

        // ---- Buttons. Each verb is distinct (Launch / Run / Show / Open /
        // Close) so adjacent buttons don't share a leading word. Height 40
        // gives breathing room for the text — 32 was clipping descenders on
        // some DPI scales.
        _btnOpenSurface = MakeButton("Launch Surface",        140);
        _btnRunTest     = MakeButton("Run Test",              110);
        _btnOpenFolder  = MakeButton("Show output folder",    160);
        _btnOpenGitHub  = MakeButton("Open GitHub thread",    160);
        _btnClose       = MakeButton("Close",                  80);

        _btnOpenSurface.Click += async (_, _) => await OnOpenSurfaceAsync();
        _btnRunTest.Click     += async (_, _) => await OnRunTestAsync();
        _btnOpenFolder.Click  += (_, _) => OpenFolder();
        _btnOpenGitHub.Click  += (_, _) => OpenUrl(GitHubIssueUrl);
        _btnClose.Click       += (_, _) => Close();

        // ---- Layout. Button row docks to bottom with explicit height so it
        // ALWAYS gets its space, regardless of how much room the instructions
        // label wants. Content fills the remaining area above. Adding content
        // first, button row second — WinForms processes docks in Z-order so
        // Bottom-docked btnRow reserves its strip before Fill claims the rest.
        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 56,
            Padding = new Padding(0, 12, 0, 0),
            WrapContents = false,
            AutoSize = false
        };
        btnRow.Controls.Add(_btnOpenSurface);
        btnRow.Controls.Add(_btnRunTest);
        btnRow.Controls.Add(_btnOpenFolder);
        btnRow.Controls.Add(_btnOpenGitHub);
        btnRow.Controls.Add(_btnClose);

        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        content.Controls.Add(title);
        content.Controls.Add(_instructions);
        content.Controls.Add(_status);

        Controls.Add(content);   // Fill — added first
        Controls.Add(btnRow);    // Bottom — added second, reserves its strip
        AcceptButton = _btnRunTest;
        CancelButton = _btnClose;
    }

    private static string ComposeInstructionsText() =>
        "This tool captures info about your Surface app's UI so the developer "
      + "can support your device. Nothing is sent automatically — the report is "
      + "saved next to this .exe; you'll manually post it to a GitHub thread.\n"
      + "\n"
      + "Steps:\n"
      + "  1. Open the Microsoft Surface app and navigate to the page that's "
          + "failing (e.g. Battery & charging).\n"
      + "  2. Click 'Run Test' below. Takes ~15-30 seconds.\n"
      + "  3. Attach the resulting .txt + .png files as a comment on the "
          + "diagnostic-results thread:\n"
      + "     " + GitHubIssueUrl;

    private static Button MakeButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 40,
        Margin = new Padding(0, 0, 8, 0)
    };

    // ---- Handlers ------------------------------------------------------

    private async Task OnOpenSurfaceAsync()
    {
        SetStatus("Resolving Surface app...");
        SetButtonsEnabled(false);
        try
        {
            var info = await Task.Run(SurfaceApp.Resolve);
            if (info == null)
            {
                MessageBox.Show(this,
                    "No Microsoft Surface app package found on this device.\n\n"
                  + "Install or reinstall it from the Microsoft Store, then try again.",
                    "Surface app not found",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("Status: Surface app not installed.");
                return;
            }

            var existing = SurfaceApp.FindRunningProcess();
            if (existing != null)
            {
                existing.Dispose();
                SetStatus("Status: Surface app already running.");
                return;
            }

            SetStatus("Launching Surface app...");
            await Task.Run(() => SurfaceApp.Launch(info.Aumid));
            await Task.Delay(2500);
            SetStatus("Status: Surface app launched. Navigate to the failing page, then click Run Test.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open Surface app:\n" + ex.Message,
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Status: error opening Surface app.");
        }
        finally { SetButtonsEnabled(true); }
    }

    private async Task OnRunTestAsync()
    {
        SetStatus("Starting diagnostic run...");
        SetButtonsEnabled(false);

        var progress = new Progress<string>(SetStatus);
        DiagnosticRunner.Result? result = null;
        try
        {
            result = await Task.Run(() => DiagnosticRunner.Run(s => ((IProgress<string>)progress).Report(s)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Diagnostic run crashed:\n" + ex,
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Status: diagnostic crashed (see message).");
            SetButtonsEnabled(true);
            return;
        }
        finally { SetButtonsEnabled(true); }

        if (result.Success)
        {
            SetStatus($"Status: ✓ Done. {result.ElementsCaptured} UIA elements captured.");
            ShowSuccessDialog(result);
        }
        else
        {
            SetStatus("Status: completed with errors (file still saved).");
            MessageBox.Show(this,
                $"{result.Message}\n\nFiles saved:\n  {result.TxtPath}"
                  + (result.PngPath != null ? $"\n  {result.PngPath}" : "")
                  + $"\n\nPlease attach them to a new GitHub issue:\n  {GitHubIssueUrl}",
                "Diagnostic — partial result",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowSuccessDialog(DiagnosticRunner.Result result)
    {
        var msg = "Diagnostic complete!\n\n"
                + "Files saved next to this tool:\n"
                + $"  • {Path.GetFileName(result.TxtPath)}\n"
                + (result.PngPath != null ? $"  • {Path.GetFileName(result.PngPath)}\n" : "")
                + $"\nNext step: open a new GitHub issue and attach both files:\n  {GitHubIssueUrl}";

        // Custom dialog with [Open output folder] [Open GitHub issue] [Close]
        using var dlg = new Form
        {
            Text = "Diagnostic complete",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(540, 230),
            Padding = new Padding(20),
            Font = Font
        };

        var lbl = new Label
        {
            Text = msg,
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Margin = new Padding(0, 0, 0, 16)
        };

        var btnFolder = MakeButton("Show output folder",  170);
        var btnIssue  = MakeButton("Open GitHub thread",  170);
        var btnDone   = MakeButton("Close",                80);
        btnFolder.Click += (_, _) => OpenFolder();
        btnIssue.Click  += (_, _) => OpenUrl(GitHubIssueUrl);
        btnDone.Click   += (_, _) => dlg.Close();

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        row.Controls.Add(btnFolder);
        row.Controls.Add(btnIssue);
        row.Controls.Add(btnDone);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true
        };
        root.Controls.Add(lbl);
        root.Controls.Add(row);
        dlg.Controls.Add(root);
        dlg.AcceptButton = btnDone;
        dlg.CancelButton = btnDone;
        dlg.ShowDialog(this);
    }

    // ---- Helpers -------------------------------------------------------

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
        _status.Text = "Status: " + text;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _btnOpenSurface.Enabled = enabled;
        _btnRunTest.Enabled     = enabled;
        _btnOpenFolder.Enabled  = enabled;
    }

    private static void OpenFolder()
    {
        try
        {
            var folder = AppContext.BaseDirectory;
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
        catch { }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
