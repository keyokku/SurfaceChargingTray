using System.IO;

namespace SurfaceChargingTray;

/// <summary>Per-action hotkey config (AHK-style key string + enabled flag).</summary>
internal class HotkeyEntry
{
    public bool Enabled { get; set; }
    public string Key { get; set; } = "";
}

internal class SettingsModel
{
    public Dictionary<string, HotkeyEntry> Hotkeys { get; set; } = new();

    /// <summary>Detected AUMID of the Surface app, cached so we don't re-discover on every run.</summary>
    public string? SurfaceAumid { get; set; }

    public static readonly Dictionary<string, HotkeyEntry> Defaults = new()
    {
        { "adaptive",  new HotkeyEntry { Enabled = false, Key = "^+1" } }, // Ctrl+Shift+1
        { "80",        new HotkeyEntry { Enabled = false, Key = "^+2" } },
        { "100-1day",  new HotkeyEntry { Enabled = false, Key = "^+3" } },
        { "100-1week", new HotkeyEntry { Enabled = false, Key = "^+4" } },
        { "cycle",     new HotkeyEntry { Enabled = false, Key = "^+B" } }
    };

    public static SettingsModel Load()
    {
        var s = new SettingsModel();
        foreach (var (k, v) in Defaults)
            s.Hotkeys[k] = new HotkeyEntry { Enabled = v.Enabled, Key = v.Key };

        if (!File.Exists(Paths.Settings)) return s;

        try
        {
            string section = "";
            foreach (var raw in File.ReadAllLines(Paths.Settings))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim().ToLowerInvariant();
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();

                if (section == "hotkeys")
                {
                    if (key.EndsWith("_enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        var action = key[..^"_enabled".Length];
                        if (s.Hotkeys.TryGetValue(action, out var e))
                            e.Enabled = (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (key.EndsWith("_key", StringComparison.OrdinalIgnoreCase))
                    {
                        var action = key[..^"_key".Length];
                        if (s.Hotkeys.TryGetValue(action, out var e))
                            e.Key = val;
                    }
                }
                else if (section == "surface" && key.Equals("aumid", StringComparison.OrdinalIgnoreCase))
                {
                    s.SurfaceAumid = val;
                }
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var lines = new List<string> { "[hotkeys]" };
            foreach (var (action, h) in Hotkeys)
            {
                lines.Add($"{action}_enabled={(h.Enabled ? 1 : 0)}");
                lines.Add($"{action}_key={h.Key}");
            }
            if (!string.IsNullOrEmpty(SurfaceAumid))
            {
                lines.Add("");
                lines.Add("[surface]");
                lines.Add($"aumid={SurfaceAumid}");
            }
            File.WriteAllLines(Paths.Settings, lines);
        }
        catch { }
    }
}
