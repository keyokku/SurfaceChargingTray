using System.Management;

namespace SurfaceChargingTray;

/// <summary>
/// Reads battery health metadata via WMI for the Battery Health menu item
/// (new in v1.4.0). Returns a pre-formatted short label + full tooltip text
/// so the caller can stuff them straight into a ToolStripMenuItem without
/// any rendering logic on its side.
///
/// Cached for 24h in settings.ini by the caller — these values change
/// roughly daily (cycle count tick, full charge capacity drift) so a
/// WMI probe on every menu hover would be wasteful.
///
/// Tolerant of partial data: any field that fails to read becomes "?" in
/// the rendered text. Never throws.
/// </summary>
internal static class BatteryHealthReader
{
    public sealed class Result
    {
        /// <summary>One-line summary suitable for a menu-item caption.
        /// e.g. "Battery: 92% (148 cycles)".</summary>
        public string Summary { get; set; } = "";
        /// <summary>Multi-line tooltip shown on hover.</summary>
        public string Tooltip { get; set; } = "";
    }

    public static Result Read()
    {
        int?    designCapacityMWh  = null;
        int?    fullChargeMWh      = null;
        int?    cycleCount         = null;
        string? chemistry          = null;
        string? manufacturer       = null;
        string? deviceName         = null;

        // root\WMI BatteryStaticData: design capacity (mWh), chemistry, mfg date.
        // Available on most laptops/tablets; missing on some virtualized systems.
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM BatteryStaticData");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    designCapacityMWh = SafeInt(mo, "DesignedCapacity");
                    chemistry         = SafeStringFromBytes(mo, "Chemistry");
                    manufacturer      = SafeStringFromBytes(mo, "ManufactureName");
                    deviceName        = SafeStringFromBytes(mo, "DeviceName");
                    break; // first battery only
                }
            }
        }
        catch { }

        // root\WMI BatteryFullChargedCapacity: actual full-charge mWh (degrades over time).
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM BatteryFullChargedCapacity");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    fullChargeMWh = SafeInt(mo, "FullChargedCapacity");
                    break;
                }
            }
        }
        catch { }

        // root\WMI BatteryCycleCount: charge-discharge cycles to date.
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM BatteryCycleCount");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    cycleCount = SafeInt(mo, "CycleCount");
                    break;
                }
            }
        }
        catch { }

        var r = new Result();

        // --- Summary line (menu caption) ---
        // Prefer the most compact format that conveys the two most useful
        // numbers: retention % and cycle count.
        if (designCapacityMWh.HasValue && fullChargeMWh.HasValue && designCapacityMWh.Value > 0)
        {
            int retention = (int)Math.Round((double)fullChargeMWh.Value / designCapacityMWh.Value * 100);
            r.Summary = cycleCount.HasValue
                ? $"Battery health: {retention}% ({cycleCount.Value} cycles)"
                : $"Battery health: {retention}%";
        }
        else if (cycleCount.HasValue)
        {
            r.Summary = $"Battery health: {cycleCount.Value} cycles";
        }
        else
        {
            r.Summary = "Battery health: (not available)";
        }

        // --- Tooltip (hover) — full breakdown ---
        var tt = new System.Text.StringBuilder();
        tt.AppendLine(deviceName ?? "Battery");
        if (!string.IsNullOrWhiteSpace(manufacturer)) tt.AppendLine($"Manufacturer: {manufacturer}");
        if (!string.IsNullOrWhiteSpace(chemistry))    tt.AppendLine($"Chemistry: {chemistry}");
        if (designCapacityMWh.HasValue)               tt.AppendLine($"Design capacity: {FormatWh(designCapacityMWh.Value)}");
        if (fullChargeMWh.HasValue)                   tt.AppendLine($"Full charge capacity: {FormatWh(fullChargeMWh.Value)}");
        if (designCapacityMWh.HasValue && fullChargeMWh.HasValue && designCapacityMWh.Value > 0)
        {
            int retention = (int)Math.Round((double)fullChargeMWh.Value / designCapacityMWh.Value * 100);
            tt.AppendLine($"Retention: {retention}%");
        }
        if (cycleCount.HasValue) tt.AppendLine($"Cycle count: {cycleCount.Value}");

        // Clarifying note: full-charge capacity is a fuel-gauge ESTIMATE that
        // re-calibrates over time and can swing several % day to day without
        // any real change in the battery. Users should watch the long-term
        // trend, not a single reading. Prevents "my battery dropped 6% in a
        // day!" panic over what's just measurement noise.
        tt.AppendLine();
        tt.AppendLine("Note: capacity is an estimate that fluctuates day to day");
        tt.AppendLine("as the battery gauge recalibrates. Watch the long-term");
        tt.AppendLine("trend rather than a single reading. A full 0->100% charge");
        tt.AppendLine("cycle helps the gauge stay accurate.");

        r.Tooltip = tt.ToString().TrimEnd();
        return r;
    }

    private static int? SafeInt(ManagementObject mo, string property)
    {
        try
        {
            var v = mo[property];
            if (v == null) return null;
            return Convert.ToInt32(v);
        }
        catch { return null; }
    }

    /// <summary>
    /// WMI returns several string-ish fields (Chemistry, ManufactureName,
    /// DeviceName) as byte arrays — null-terminated ASCII. Convert safely.
    /// </summary>
    private static string? SafeStringFromBytes(ManagementObject mo, string property)
    {
        try
        {
            var v = mo[property];
            if (v is byte[] bytes)
            {
                // Trim trailing zeros (null terminator + padding)
                int len = bytes.Length;
                while (len > 0 && bytes[len - 1] == 0) len--;
                if (len == 0) return null;
                var str = System.Text.Encoding.ASCII.GetString(bytes, 0, len).Trim();
                return string.IsNullOrEmpty(str) ? null : str;
            }
            return v?.ToString();
        }
        catch { return null; }
    }

    private static string FormatWh(int mWh)
    {
        // Show with one decimal place to keep tooltip compact ("37.4 Wh"
        // vs raw "37,450 mWh"). Round to nearest tenth.
        var wh = mWh / 1000.0;
        return $"{wh:F1} Wh ({mWh:N0} mWh)";
    }
}
