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

    // ---- Charging mode scheduler (v1.2.0+, multi-slot v1.4.0+) ---------
    //
    // v1.2.x-v1.3.x stored a single armed schedule (mode + duration + time).
    // v1.4.0 generalizes to a LIST of up to 3 schedule slots, each with its
    // own mode/duration/time, so a user can e.g. flip to 100% at 06:00 and
    // back to 80% at 09:00 in one overnight simulated-sleep run.
    //
    // When the user presses the schedule-toggle hotkey, the tray enters
    // fake-sleep and arms a timer per slot (computed from each slot's
    // next-occurrence). ScheduleAutoExit governs when fake-sleep tears down.
    //
    // Migration: old single-schedule settings.ini ([schedule] mode/duration/
    // time/auto_exit) loads as a single slot; the old auto_exit bool maps to
    // AfterFirst (1) or Stay (0).

    public sealed class ScheduleEntry
    {
        /// <summary>"adaptive" / "80" / "100" / "oneshot".</summary>
        public string  Mode     { get; set; } = "";
        /// <summary>"1day" / "1week" — only meaningful when Mode == "100".</summary>
        public string? Duration { get; set; }
        /// <summary>"HH:mm" 24h.</summary>
        public string  Time     { get; set; } = "";
    }

    /// <summary>When to tear down simulated sleep relative to scheduled fires.</summary>
    public enum ScheduleExitMode
    {
        /// <summary>Never auto-exit; user dismisses manually.</summary>
        Stay,
        /// <summary>Exit immediately after the first scheduled fire runs.</summary>
        AfterFirst,
        /// <summary>Exit only after the last (latest-time) scheduled fire runs.</summary>
        AfterAll
    }

    /// <summary>0-3 schedule slots. Empty = no schedule armed.</summary>
    public List<ScheduleEntry> Schedules { get; set; } = new();

    /// <summary>Auto-exit behavior. Default AfterFirst preserves v1.2.x single-
    /// slot behavior where auto_exit=1 meant "exit after the one fire".</summary>
    public ScheduleExitMode ScheduleAutoExit { get; set; } = ScheduleExitMode.AfterFirst;

    /// <summary>Hard cap on schedule slots — enforced by the Settings UI.</summary>
    public const int MaxScheduleSlots = 3;

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

    // ---- Background tracking (v1.4.0+) ---------------------------------
    //
    // Lightweight state that persists across launches: battery health
    // caching (so we don't WMI-probe on every menu hover), update-checker
    // throttling (one HTTP call per day max), and calibration-reminder
    // tracking (note when battery last hit 100%, and once-per-cycle flag
    // so the reminder doesn't repeat).
    //
    // Stored in a separate [tracking] section in settings.ini to keep the
    // user-facing config (hotkeys, schedule) easy to inspect.

    /// <summary>ISO-8601 timestamp of the last successful battery-health read. Empty = never read.</summary>
    public string? BatteryHealthCheckedAt { get; set; }
    /// <summary>Short label rendered as a menu-item caption (e.g. "92% (148 cycles)"). Cached.</summary>
    public string? BatteryHealthSummary   { get; set; }
    /// <summary>Full hover tooltip text — capacity, design, manufacture date. Cached.</summary>
    public string? BatteryHealthTooltip   { get; set; }

    /// <summary>ISO-8601 timestamp of the last GitHub releases API check. Throttled to once/day.</summary>
    public string? LastUpdateCheckAt      { get; set; }
    /// <summary>Latest version tag observed on GitHub (e.g. "v1.4.0"). Null = no check yet.</summary>
    public string? LatestKnownVersion     { get; set; }

    /// <summary>ISO-8601 timestamp of the last time battery reached 100%. Empty = never observed.</summary>
    public string? LastFullChargeAt       { get; set; }
    /// <summary>True if calibration reminder fired this cycle (resets on next 100% reach).</summary>
    public bool   CalibrationReminderShown { get; set; }

    // ---- Low-battery warning (v1.4.2+) ---------------------------------
    //
    // A user-configurable toast when the battery drops to a threshold while
    // on battery power. Separate from Windows' own low-battery notification
    // (which defaults to ~10%); this gives a reliable, app-controlled alert
    // at a level the user picks. Fires once per discharge cycle.

    /// <summary>Enable the low-battery toast. Default on.</summary>
    public bool LowBatteryWarnEnabled { get; set; } = true;
    /// <summary>Battery percent at which to warn (on battery only). Default 20.</summary>
    public int  LowBatteryWarnPct     { get; set; } = 20;

    public static readonly Dictionary<string, HotkeyEntry> Defaults = new()
    {
        // Charging modes
        { "adaptive",        new HotkeyEntry { Enabled = false, Key = "^+1" } },
        { "80",              new HotkeyEntry { Enabled = false, Key = "^+2" } },
        { "100-1day",        new HotkeyEntry { Enabled = false, Key = "^+3" } },
        { "100-1week",       new HotkeyEntry { Enabled = false, Key = "^+4" } },
        { "cycle",           new HotkeyEntry { Enabled = false, Key = "^+B" } },
        // Variant B: single one-shot 'Charge to 100%' override. v1.3.0+.
        // Default Ctrl+Shift+1 — same key slot as 'adaptive' on variant A.
        // No collision in practice because ApplyHotkeys filters action
        // registrations by current detected variant: variant A skips
        // 'oneshot', variant B skips 'adaptive'/'80'/'100-*'/'cycle'.
        { "oneshot",         new HotkeyEntry { Enabled = false, Key = "^+1" } },
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
            // Schedule slot accumulators — slotN_* keys can arrive in any
            // order, so collect into a sorted dict keyed by slot index, then
            // materialize into s.Schedules after the parse loop. Old single-
            // schedule keys (mode/duration/time) accumulate separately and
            // are migrated to slot 0 only if no slotN keys were present.
            var slotEntries = new SortedDictionary<int, ScheduleEntry>();
            string? legacyMode = null, legacyDuration = null, legacyTime = null;

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
                    var lkey = key.ToLowerInvariant();
                    // New multi-slot keys: slot0_mode, slot0_duration, slot0_time, slot1_mode, ...
                    if (lkey.StartsWith("slot"))
                    {
                        int us = lkey.IndexOf('_');
                        if (us > 4 && int.TryParse(lkey[4..us], out int idx))
                        {
                            if (!slotEntries.TryGetValue(idx, out var entry))
                            {
                                entry = new ScheduleEntry();
                                slotEntries[idx] = entry;
                            }
                            var field = lkey[(us + 1)..];
                            switch (field)
                            {
                                case "mode":     entry.Mode     = val; break;
                                case "duration": entry.Duration = string.IsNullOrEmpty(val) ? null : val; break;
                                case "time":     entry.Time     = val; break;
                            }
                        }
                    }
                    else
                    {
                        switch (lkey)
                        {
                            // Legacy single-schedule keys (v1.2.x-v1.3.x). Held
                            // aside; migrated to slot 0 only if no slotN keys.
                            case "mode":      legacyMode     = string.IsNullOrEmpty(val) ? null : val; break;
                            case "duration":  legacyDuration = string.IsNullOrEmpty(val) ? null : val; break;
                            case "time":      legacyTime     = string.IsNullOrEmpty(val) ? null : val; break;
                            case "auto_exit": s.ScheduleAutoExit = ParseExitMode(val); break;
                        }
                    }
                }
                else if (section == "tracking")
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "battery_health_checked_at":   s.BatteryHealthCheckedAt   = val; break;
                        case "battery_health_summary":      s.BatteryHealthSummary     = val; break;
                        case "battery_health_tooltip":      s.BatteryHealthTooltip     = val; break;
                        case "last_update_check_at":        s.LastUpdateCheckAt        = val; break;
                        case "latest_known_version":        s.LatestKnownVersion       = val; break;
                        case "last_full_charge_at":         s.LastFullChargeAt         = val; break;
                        case "calibration_reminder_shown":  s.CalibrationReminderShown = (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase)); break;
                    }
                }
                else if (section == "notifications")
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "low_battery_warn_enabled":
                            s.LowBatteryWarnEnabled = (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase));
                            break;
                        case "low_battery_warn_pct":
                            if (int.TryParse(val, out int lp) && lp >= 1 && lp <= 99) s.LowBatteryWarnPct = lp;
                            break;
                    }
                }
            }

            // Materialize schedule slots. Prefer new slotN entries; fall back
            // to migrating the legacy single-schedule keys into slot 0.
            if (slotEntries.Count > 0)
            {
                foreach (var kv in slotEntries)
                {
                    var e = kv.Value;
                    // Skip incomplete slots (mode + time both required).
                    if (!string.IsNullOrEmpty(e.Mode) && !string.IsNullOrEmpty(e.Time))
                        s.Schedules.Add(e);
                }
            }
            else if (!string.IsNullOrEmpty(legacyMode) && !string.IsNullOrEmpty(legacyTime))
            {
                s.Schedules.Add(new ScheduleEntry
                {
                    Mode     = legacyMode!,
                    Duration = legacyDuration,
                    Time     = legacyTime!
                });
            }
            // Enforce the slot cap even if settings.ini was hand-edited.
            if (s.Schedules.Count > MaxScheduleSlots)
                s.Schedules = s.Schedules.GetRange(0, MaxScheduleSlots);
        }
        catch { }
        return s;
    }

    /// <summary>
    /// Parses the auto_exit value. Accepts the new enum names
    /// (stay/after_first/after_all) AND the legacy bool form (0/1/true/false),
    /// mapping 1/true -> AfterFirst (old "exit after the one fire" behavior)
    /// and 0/false -> Stay.
    /// </summary>
    private static ScheduleExitMode ParseExitMode(string val)
    {
        switch (val.Trim().ToLowerInvariant())
        {
            case "stay":         return ScheduleExitMode.Stay;
            case "after_first":  return ScheduleExitMode.AfterFirst;
            case "after_all":    return ScheduleExitMode.AfterAll;
            case "1":
            case "true":         return ScheduleExitMode.AfterFirst;   // legacy bool
            default:             return ScheduleExitMode.Stay;          // "0"/"false"/unknown
        }
    }

    private static string ExitModeToString(ScheduleExitMode m) => m switch
    {
        ScheduleExitMode.AfterFirst => "after_first",
        ScheduleExitMode.AfterAll   => "after_all",
        _                           => "stay"
    };

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
            // Emit schedule section only if there's at least one slot OR a
            // non-default exit mode. Keeps settings.ini tidy when no schedule
            // is armed. Multi-slot format: slotN_mode / slotN_duration /
            // slotN_time, plus auto_exit as the enum string.
            if (Schedules.Count > 0 || ScheduleAutoExit != ScheduleExitMode.AfterFirst)
            {
                lines.Add("");
                lines.Add("[schedule]");
                lines.Add($"auto_exit={ExitModeToString(ScheduleAutoExit)}");
                for (int i = 0; i < Schedules.Count; i++)
                {
                    var e = Schedules[i];
                    if (string.IsNullOrEmpty(e.Mode) || string.IsNullOrEmpty(e.Time)) continue;
                    AppendIfSet(lines, $"slot{i}_mode",     e.Mode);
                    AppendIfSet(lines, $"slot{i}_duration", e.Duration);
                    AppendIfSet(lines, $"slot{i}_time",     e.Time);
                }
            }
            // Emit tracking section only if any field is non-default.
            if (!string.IsNullOrEmpty(BatteryHealthCheckedAt) ||
                !string.IsNullOrEmpty(BatteryHealthSummary)   ||
                !string.IsNullOrEmpty(BatteryHealthTooltip)   ||
                !string.IsNullOrEmpty(LastUpdateCheckAt)      ||
                !string.IsNullOrEmpty(LatestKnownVersion)     ||
                !string.IsNullOrEmpty(LastFullChargeAt)       ||
                CalibrationReminderShown)
            {
                lines.Add("");
                lines.Add("[tracking]");
                AppendIfSet(lines, "battery_health_checked_at",  BatteryHealthCheckedAt);
                AppendIfSet(lines, "battery_health_summary",     BatteryHealthSummary);
                AppendIfSet(lines, "battery_health_tooltip",     BatteryHealthTooltip);
                AppendIfSet(lines, "last_update_check_at",       LastUpdateCheckAt);
                AppendIfSet(lines, "latest_known_version",       LatestKnownVersion);
                AppendIfSet(lines, "last_full_charge_at",        LastFullChargeAt);
                lines.Add($"calibration_reminder_shown={(CalibrationReminderShown ? 1 : 0)}");
            }
            // Emit notifications section only if non-default (disabled, or a
            // threshold other than 20%). Keeps a fresh settings.ini tidy.
            if (!LowBatteryWarnEnabled || LowBatteryWarnPct != 20)
            {
                lines.Add("");
                lines.Add("[notifications]");
                lines.Add($"low_battery_warn_enabled={(LowBatteryWarnEnabled ? 1 : 0)}");
                lines.Add($"low_battery_warn_pct={LowBatteryWarnPct}");
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
