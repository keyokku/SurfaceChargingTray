using System.IO;
using System.Text.Json;

namespace SurfaceChargingTray;

/// <summary>Persisted record of the most recently set charging mode.</summary>
internal record StateRecord(string? Mode, string? Duration, string Timestamp)
{
    public static StateRecord Empty => new(null, null, "");
}

internal static class StateStore
{
    public static string CachePath =>
        Path.Combine(Paths.DataDir, "surface-state.json");

    public static StateRecord Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return StateRecord.Empty;
            var json = File.ReadAllText(CachePath);
            var doc = JsonDocument.Parse(json);
            string? mode = null, duration = null, ts = "";
            if (doc.RootElement.TryGetProperty("Mode", out var m) && m.ValueKind == JsonValueKind.String)
                mode = m.GetString();
            if (doc.RootElement.TryGetProperty("Duration", out var d) && d.ValueKind == JsonValueKind.String)
                duration = d.GetString();
            if (doc.RootElement.TryGetProperty("Timestamp", out var t) && t.ValueKind == JsonValueKind.String)
                ts = t.GetString() ?? "";
            return new StateRecord(mode, duration, ts);
        }
        catch { return StateRecord.Empty; }
    }

    public static void Save(string mode, string? duration)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var rec = new
            {
                Mode = mode,
                Duration = mode == "100" ? duration : null,
                Timestamp = DateTime.Now.ToString("s")
            };
            var json = JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CachePath, json);
        }
        catch { }
    }
}

internal static class Paths
{
    /// <summary>
    /// Folder containing the running .exe — also where settings.ini,
    /// surface-state.json, and surface-error.log live. Keeping data files
    /// next to the exe makes the whole package portable: copying the folder
    /// somewhere else takes the configuration with it.
    /// </summary>
    public static string DataDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string ErrorLog => Path.Combine(DataDir, "surface-error.log");
    public static string CrashLog => Path.Combine(DataDir, "crash.log");
    public static string Settings => Path.Combine(DataDir, "settings.ini");
}
