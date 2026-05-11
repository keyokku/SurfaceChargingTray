using System.Windows.Forms;

namespace SurfaceChargingTray;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // CLI mode: --set-mode runs headlessly (no tray, no UI), executes the
        // mode change, exits with 0 on success / non-zero on failure. Used by
        // the v1.2.0+ Charging Mode Scheduler — Windows Task Scheduler invokes
        // us with these args at the user's chosen time.
        //
        // Important: bypass the single-instance mutex. The user's tray is
        // probably already running when a scheduled task fires; we do NOT
        // want the CLI invocation to no-op silently because the mutex is
        // taken. UI Automation is fine with two processes hitting the
        // Surface app sequentially.
        if (CliMode.IsCliInvocation(args))
            return CliMode.Run(args);

        // Single-instance guard so a second launch silently no-ops.
        using var mutex = new Mutex(true,
            "SurfaceChargingTray-{B7E2D4F0-7A1E-4F0B-9C8F-1D5A2C9E4B6A}", out bool created);
        if (!created) return 0;

        // Catch every unhandled exception we can and write a crash log,
        // instead of letting WinForms' default ThreadExceptionDialog (which
        // itself can fail) tear the process down with no diagnostics.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HandleCrash("ThreadException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HandleCrash("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            HandleCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        DarkMode.AllowDarkModeForApp();

        // Heartbeat: a single line in surface-error.log so absence of any
        // log after a click means the process died before reaching the
        // event handler (vs. ran fine but didn't log anything).
        try
        {
            var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "?";
            Logger.Started(version);
        }
        catch { }

        // If a previous run crashed mid-fake-sleep, restore the user's
        // brightness and Power mode before the tray comes up. Silent no-op
        // if no recovery file is present.
        try { FakeSleepMode.RestoreOnStartup(); }
        catch (Exception ex) { Logger.Crash("FakeSleepMode.RestoreOnStartup", ex); }

        Application.Run(new TrayAppContext());
        return 0;
    }

    static void HandleCrash(string source, Exception? ex)
    {
        Logger.Crash(source, ex);
        try
        {
            MessageBox.Show(
                ex?.Message ?? "Unknown error",
                "Surface Charging Tray — error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* MessageBox itself can fail; give up silently */ }
    }
}
