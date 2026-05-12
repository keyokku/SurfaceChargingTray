using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SurfaceChargingTray;

/// <summary>
/// Custom replacement for the plain MessageBox previously shown by
/// <see cref="TrayAppContext.ShowLastError"/>. When the error message
/// matches a known "detection failed on this device" pattern, this dialog
/// adds clickable buttons that link straight to the diagnostic tool
/// download and the diagnostic-results GitHub thread, so users have a
/// one-click path to help us debug instead of needing to read the README.
/// </summary>
internal static class ErrorDialog
{
    private const string ReleasesUrl =
        "https://github.com/keyokku/SurfaceChargingTray/releases/latest";
    private const string ThreadUrl =
        "https://github.com/keyokku/SurfaceChargingTray/issues/2";

    public static void Show(IWin32Window? owner, string errorMessage)
    {
        // Heuristic: the v1.2.x detection-failure errors all reference
        // "card", "radio", or "supported" wording. Anything else (e.g.
        // hotkey registration failure) gets the plain dialog without
        // the diagnostic-help section, which would be off-topic noise.
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
            ClientSize = showDiagnosticHelp ? new Size(560, 360) : new Size(520, 200),
            MinimumSize = showDiagnosticHelp ? new Size(500, 320) : new Size(420, 180)
        };

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 56,
            Padding = new Padding(0, 12, 0, 0),
            AutoSize = false,
            WrapContents = false
        };

        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        // ---- Error message
        content.Controls.Add(new Label
        {
            Text = errorMessage,
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            ForeColor = Color.FromArgb(0xB0, 0x2A, 0x2A),
            Margin = new Padding(0, 0, 0, 12)
        });

        // ---- Diagnostic-help block (only when relevant)
        Button btnOk;
        if (showDiagnosticHelp)
        {
            content.Controls.Add(new Label
            {
                Text = "Looks like the Surface app's UI on your device doesn't match what "
                     + "this tool expects. The diagnostic tool captures your Surface app's "
                     + "structure so I can support your model in a future update.",
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Margin = new Padding(0, 0, 0, 12)
            });

            content.Controls.Add(new Label
            {
                Text = "Steps:",
                AutoSize = true,
                Font = new Font(form.Font, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 4)
            });

            content.Controls.Add(new Label
            {
                Text = "  1. Download SurfaceChargingTrayDiagnostic.zip from Releases below.\n"
                     + "  2. Run the .exe matching your CPU (x64 or arm64).\n"
                     + "  3. Click 'Run Test' in the diagnostic dialog.\n"
                     + "  4. Post the .txt + .png it produces on the GitHub thread.",
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Margin = new Padding(0, 0, 0, 12)
            });

            var btnReleases = MakeButton("Download diagnostic tool", 200);
            var btnThread   = MakeButton("Open GitHub thread",       170);
            btnOk           = MakeButton("Close",                     80);
            btnReleases.Click += (_, _) => OpenUrl(ReleasesUrl);
            btnThread.Click   += (_, _) => OpenUrl(ThreadUrl);
            btnOk.Click       += (_, _) => form.Close();
            btnRow.Controls.Add(btnReleases);
            btnRow.Controls.Add(btnThread);
            btnRow.Controls.Add(btnOk);
        }
        else
        {
            btnOk = MakeButton("Close", 80);
            btnOk.Click += (_, _) => form.Close();
            btnRow.Controls.Add(btnOk);
        }

        form.Controls.Add(content);
        form.Controls.Add(btnRow);
        form.AcceptButton = btnOk;
        form.CancelButton = btnOk;
        return form;
    }

    private static Button MakeButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 40,
        Margin = new Padding(0, 0, 8, 0)
    };

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
