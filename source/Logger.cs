using System.IO;

namespace SurfaceChargingTray;

/// <summary>
/// Persistent rotating log writer for both surface-error.log and crash.log.
/// Each entry carries an ISO-8601 timestamp; the file is capped at
/// <see cref="MaxLines"/> entries (oldest dropped on overflow) so the log
/// can be left enabled indefinitely without growing unbounded.
/// Files live next to the .exe via <see cref="Paths.DataDir"/>.
/// </summary>
internal static class Logger
{
    public const int MaxLines = 500;

    private static readonly object _lock = new();

    /// <summary>Append an operational error (e.g. UIA "card not found").</summary>
    public static void Error(string message)
        => AppendCapped(Paths.ErrorLog, message);

    /// <summary>Write a one-line "session started" entry to the error log.
    /// If the user later complains that nothing happens when they click X,
    /// the absence of either an error or a started entry tells us the app
    /// died before reaching this point (e.g. native AccessViolation).
    /// </summary>
    public static void Started(string version)
        => AppendCapped(Paths.ErrorLog, $"[INFO] Started v{version}");

    /// <summary>Append an unhandled-exception crash entry.</summary>
    public static void Crash(string source, Exception? ex)
    {
        var detail = ex?.ToString() ?? "(no exception object)";
        AppendCapped(Paths.CrashLog, $"{source}: {ex?.Message ?? "(no message)"}{Environment.NewLine}{detail}");
    }

    /// <summary>Most recent entry's message text (without timestamp), or empty if log is empty/missing.</summary>
    public static string LatestEntry(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return "";
            var lines = File.ReadAllLines(filePath);
            // Walk back from the bottom to find the most recent timestamp line.
            // Entries are stored as: "[timestamp] first line\n  detail line 1\n  detail line 2\n"
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].StartsWith("[", StringComparison.Ordinal))
                {
                    // Collect this line + any following indented continuation lines.
                    var collected = new System.Text.StringBuilder();
                    var head = lines[i];
                    int rb = head.IndexOf(']');
                    if (rb >= 0 && rb + 2 < head.Length)
                        collected.Append(head[(rb + 2)..]);
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        if (lines[j].Length == 0 || lines[j].StartsWith("[", StringComparison.Ordinal)) break;
                        collected.AppendLine().Append(lines[j]);
                    }
                    return collected.ToString();
                }
            }
        }
        catch { }
        return "";
    }

    private static void AppendCapped(string filePath, string message)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);

            var stamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
            var entry = $"[{stamp}] {message}";

            lock (_lock)
            {
                File.AppendAllText(filePath, entry + Environment.NewLine);

                // Cheap rotation: if we exceed the cap, rewrite keeping only
                // the newest MaxLines lines. Avoid doing this on every write
                // — only when over by >10% to amortise.
                var lines = File.ReadAllLines(filePath);
                if (lines.Length > MaxLines + (MaxLines / 10))
                {
                    var trimmed = lines[(lines.Length - MaxLines)..];
                    File.WriteAllLines(filePath, trimmed);
                }
            }
        }
        catch { /* logger must never throw */ }
    }

    /// <summary>Clear the operational error log (e.g. after a successful action).</summary>
    public static void ClearErrors()
    {
        try { if (File.Exists(Paths.ErrorLog)) File.Delete(Paths.ErrorLog); }
        catch { }
    }
}
