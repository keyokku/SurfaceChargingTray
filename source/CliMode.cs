namespace SurfaceChargingTray;

/// <summary>
/// Headless command-line entry point.
///
/// CLI surface:
///
///   --set-mode {adaptive|80|100} [--duration {1day|1week}]
///       Flip the Surface app's charging mode and exit. Useful for
///       scripting / external automation. Returns 0 on success, non-zero
///       on failure. Every run appends an [INFO] entry to surface-error.log
///       so headless invocations are visible without any UI present.
/// </summary>
internal static class CliMode
{
    public static bool IsCliInvocation(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--set-mode") return true;
        return false;
    }

    public static int Run(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--set-mode") return RunSetMode(args);
        }
        Logger.Error("[CLI] no recognized verb in args");
        return 2;
    }

    private static int RunSetMode(string[] args)
    {
        string? mode = null;
        string? duration = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--set-mode" && i + 1 < args.Length)  mode = args[++i];
            else if (args[i] == "--duration" && i + 1 < args.Length) duration = args[++i];
        }

        if (string.IsNullOrEmpty(mode))
        {
            Logger.Error("[CLI] --set-mode argument missing or empty");
            return 2;
        }
        if (mode != "adaptive" && mode != "80" && mode != "100")
        {
            Logger.Error($"[CLI] --set-mode '{mode}' is not one of: adaptive, 80, 100");
            return 2;
        }
        if (mode == "100" && duration != null && duration != "1day" && duration != "1week")
        {
            Logger.Error($"[CLI] --duration '{duration}' is not one of: 1day, 1week");
            return 2;
        }

        var versionStr = typeof(CliMode).Assembly.GetName().Version?.ToString() ?? "?";
        Logger.Error($"[INFO] CLI v{versionStr}: --set-mode={mode}" +
                     (duration != null ? $" --duration={duration}" : ""));

        try
        {
            var settings = SettingsModel.Load();
            SurfaceController.Aumid = AumidResolver.Resolve(settings);
            SurfaceController.Settings = settings;

            var err = SurfaceController.SetMode(mode, duration);
            if (err != null)
            {
                Logger.Error($"[CLI] SetMode failed: {err}");
                return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Crash("[CLI]", ex);
            return 1;
        }
    }
}
