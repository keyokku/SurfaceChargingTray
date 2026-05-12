using System.Windows.Forms;

namespace SurfaceChargingTrayDiagnostic;

internal static class Program
{
    [STAThread]
    static int Main()
    {
        // Catch unhandled exceptions and surface them as a MessageBox rather
        // than letting WinForms' default handler tear down silently. The
        // diagnostic tool is the LAST thing that should crash without
        // telling the user.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowCrash("ThreadException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ShowCrash("UnhandledException", e.ExceptionObject as Exception);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Application.Run(new DiagnosticForm());
        return 0;
    }

    static void ShowCrash(string source, Exception? ex)
    {
        try
        {
            MessageBox.Show(
                $"{source}: {ex?.Message ?? "(no message)"}\n\n{ex}",
                "Surface Charging Tray Diagnostic — error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* MessageBox itself can fail */ }
    }
}
