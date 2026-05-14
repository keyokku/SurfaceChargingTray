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

    // ---- Charging mode scheduler (v1.2.0+) ----------------------------
    //
    // Stored as a single "armed schedule" rather than a calendar of fires.
    // When the user presses the schedule-toggle hotkey, the tray enters
    // fake-sleep and computes the next-occurrence of ScheduleTime to fire
    // a SetMode(ScheduleMode[, ScheduleDuration]). Clearing the schedule
    // means setting ScheduleTime to null — no checkbox needed, the hotkey
    // itself is the on/off.

    /// <summary>"adaptive" / "80" / "100" — null means no schedule set.</summary>
    public string? ScheduleMode { get; set; }

    /// <summary>"1day" / "1week" — only meaningful when ScheduleMode == "100".</summary>
    public string? ScheduleDuration { get; set; }

    /// <summary>"HH:mm" 24h — null means no time set (cleared schedule).</summary>
    public string? ScheduleTime { get; set; }

    /// <summary>true = tear down fake-sleep after the scheduled SetMode fires
    /// so the device falls back to its real Sleep / Screen-off timeouts.
    /// false = stay in fake-sleep until user dismisses.</summary>
    public bool ScheduleAutoExit { get; set; }

    /// <summary>
    /// Auto-discovered AutomationId / Name for the Battery & charging card and its
    /// three radio buttons. Captured on the first successful lookup so subsequent
    /// runs go straight through the AutomationId path (faster, language-independent,
    /// version-independent). Cleared if validation ever fails — see UiaCache.cs.
    /// </summary>
    public string? BatteryCardId        { get; set; }
    public string? BatteryCardName      { get; set; }
    public string? AdaptiveRadioId      { get; set; }
    public string? AdaptiveRadioName    { get; set; }
    public string? Limit80RadioId       { get; set; }
    public string? Limit80RadioName     { get; set; }
    public string? Charge100RadioId     { get; set; }
    public string? Charge100RadioName   { get; set; }

    // ---- Variant detection (v1.3.0+) -----------------------------------
    //
    // Surface devices ship two distinct UI shapes for the Battery & charging
    // card. Variant A: the three-radio classic (Adaptive / 80% / 100%) we
    // targeted in v1.0-v1.2. Variant B: a single one-shot "Charge to 100%"
    // override button — no radios, no mode concept — observed on certain
    // older Surfaces (e.g. SLS gen 1 / users with paused Smart Charging UIs).
    //
    // DetectedVariant is filled by UiaCache.DetectVariant on the first
    // successful card lookup of each launch. DetectedAtAppVersion stamps
    // which app version performed the detection, so a future release whose
    // detection logic improved automatically re-detects on first run
    // (we treat a version mismatch as "stale, re-detect").
    //
    // OneShotButtonId/Name cache the structural identifiers of the variant B
    // button so the next launch's lookup hits Layer 1 (cached AndCondition)
    // instead of re-walking the card.

    /// <summary>"A" (3 radios) / "B" (one-shot button) / "Unknown" / null (never detected).</summary>
    public string? DetectedVariant       { get; set; }

    /// <summary>The SurfaceChargingTray app version that performed the detection. Stale → re-detect.</summary>
    public string? DetectedAtAppVersion  { get; set; }

    /// <summary>Cached AutomationId of the variant B one-shot button.</summary>
    public string? OneShotButtonId       { get; set; }

    /// <summary>Cached Name of the variant B one-shot button (disambiguator, not detection key).</summary>
    public string? OneShotButtonName     { get; set; }

    public static readonly Dictionary<string, HotkeyEntry> Defaults = new()
    {
        // Charging modes
        { "adaptive",        new HotkeyEntry { Enabled = false, Key = "^+1" } },
        { "80",              new HotkeyEntry { Enabled = false, Key = "^+2" } },
        { "100-1day",        new HotkeyEntry { Enabled = false, Key = "^+3" } },
        { "100-1week",       new HotkeyEntry { Enabled = false, Key = "^+4" } },
        { "cycle",           new HotkeyEntry { Enabled = false, Key = "^+B" } },
        // Windows Power modes
        { "power-efficient", new HotkeyEntry { Enabled = false, Key = "^+5" } },
        { "power-balanced",  new HotkeyEntry { Enabled = false, Key = "^+6" } },
        { "power-perf",      new HotkeyEntry { Enabled = false, Key = "^+7" } },
        // Scheduler: one hotkey toggles simulated sleep on/off. When entering,
        // if ScheduleTime is set, the scheduled SetMode fires at that
        // time. Disabled by default; user picks the combo in Settings.
        // Default Ctrl+Shift+T — avoids colliding with the Ctrl+Shift+[1-7]
        // charging-mode / Power-mode defaults.
        { "schedule-toggle", new HotkeyEntry { Enabled = false, Key = "^+T" } },
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
                else if (section == "surface")
                {
                    if (key.Equals("aumid", StringComparison.OrdinalIgnoreCase))
                        s.SurfaceAumid = val;
                }
                else if (section == "uia-cache")
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "battery_card_id":        s.BatteryCardId        = val; break;
                        case "battery_card_name":      s.BatteryCardName      = val; break;
                        case "adaptive_radio_id":      s.AdaptiveRadioId      = val; break;
                        case "adaptive_radio_name":    s.AdaptiveRadioName    = val; break;
                        case "limit80_radio_id":       s.Limit80RadioId       = val; break;
                        case "limit80_radio_name":     s.Limit80RadioName     = val; break;
                        case "charge100_radio_id":     s.Charge100RadioId     = val; break;
                        case "charge100_radio_name":   s.Charge100RadioName   = val; break;
                        case "detected_variant":       s.DetectedVariant      = val; break;
                        case "detected_at_app_version":s.DetectedAtAppVersion = val; break;
                        case "oneshot_button_id":      s.OneShotButtonId      = val; break;
                        case "oneshot_button_name":    s.OneShotButtonName    = val; break;
                    }
                }
                else if (section == "schedule")
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "mode":      s.ScheduleMode     = string.IsNullOrEmpty(val) ? null : val; break;
                        case "duration":  s.ScheduleDuration = string.IsNullOrEmpty(val) ? null : val; break;
                        case "time":      s.ScheduleTime     = string.IsNullOrEmpty(val) ? null : val; break;
                        case "auto_exit": s.ScheduleAutoExit = (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase)); break;
                    }
                }
            }
        }
        catch { }
        return s;
    }

    private bool HasUiaCacheData() =>
        !string.IsNullOrEmpty(BatteryCardId)        || !string.IsNullOrEmpty(BatteryCardName)
     || !string.IsNullOrEmpty(AdaptiveRadioId)      || !string.IsNullOrEmpty(AdaptiveRadioName)
     || !string.IsNullOrEmpty(Limit80RadioId)       || !string.IsNullOrEmpty(Limit80RadioName)
     || !string.IsNullOrEmpty(Charge100RadioId)     || !string.IsNullOrEmpty(Charge100RadioName)
     || !string.IsNullOrEmpty(DetectedVariant)      || !string.IsNullOrEmpty(DetectedAtAppVersion)
     || !string.IsNullOrEmpty(OneShotButtonId)      || !string.IsNullOrEmpty(OneShotButtonName);

    private static void AppendIfSet(List<string> lines, string key, string? val)
    {
        if (!string.IsNullOrEmpty(val)) lines.Add($"{key}={val}");
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
            // Emit schedule section only if any of its fields are non-default.
            // Keeps settings.ini tidy on first launch / when no schedule armed.
            if (!string.IsNullOrEmpty(ScheduleMode) ||
                !string.IsNullOrEmpty(ScheduleDuration) ||
                !string.IsNullOrEmpty(ScheduleTime) ||
                ScheduleAutoExit)
            {
                lines.Add("");
                lines.Add("[schedule]");
                AppendIfSet(lines, "mode",     ScheduleMode);
                AppendIfSet(lines, "duration", ScheduleDuration);
                AppendIfSet(lines, "time",     ScheduleTime);
                lines.Add($"auto_exit={(ScheduleAutoExit ? 1 : 0)}");
            }
            // Only emit cache section if we've discovered at least one value;
            // keeps a fresh settings.ini tidy on first launch before any lookup.
            if (HasUiaCacheData())
            {
                lines.Add("");
                lines.Add("[uia-cache]");
                AppendIfSet(lines, "battery_card_id",         BatteryCardId);
                AppendIfSet(lines, "battery_card_name",       BatteryCardName);
                AppendIfSet(lines, "adaptive_radio_id",       AdaptiveRadioId);
                AppendIfSet(lines, "adaptive_radio_name",     AdaptiveRadioName);
                AppendIfSet(lines, "limit80_radio_id",        Limit80RadioId);
                AppendIfSet(lines, "limit80_radio_name",      Limit80RadioName);
                AppendIfSet(lines, "charge100_radio_id",      Charge100RadioId);
                AppendIfSet(lines, "charge100_radio_name",    Charge100RadioName);
                AppendIfSet(lines, "detected_variant",        DetectedVariant);
                AppendIfSet(lines, "detected_at_app_version", DetectedAtAppVersion);
                AppendIfSet(lines, "oneshot_button_id",       OneShotButtonId);
                AppendIfSet(lines, "oneshot_button_name",     OneShotButtonName);
            }
            File.WriteAllLines(Paths.Settings, lines);
        }
        catch { }
    }
}
