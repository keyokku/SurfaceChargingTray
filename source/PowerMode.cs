using System.Runtime.InteropServices;

namespace SurfaceChargingTray;

/// <summary>
/// Read / write the Windows 11 "Power mode" overlay (Best power efficiency
/// / Balanced / Best performance). No admin rights required.
///
/// Two Windows quirks shape this implementation:
///
/// 1. The GET function is "Effective" (the documented Win10 1709+ name),
///    not "Active" (an older undocumented name not exported on all builds —
///    notably missing on Windows 11 ARM64). Returns the currently-effective
///    overlay accounting for AC/DC state.
///
/// 2. ALL Power* set functions MUST be PInvoked with `ref Guid`, not
///    `Guid` by value. Microsoft documents the signature as `GUID guid`
///    (by value), but on ARM64 Windows passing a 16-byte struct by value
///    via P/Invoke triggers an AccessViolation (0xC0000005) inside the
///    function — the ARM64 calling convention handles 16-byte structs
///    differently than x64. Passing as `ref Guid` sends a pointer, which
///    is what the function actually reads, and works on both architectures.
///    Empty GUID happens to be 16 zero bytes which is "safe" garbage —
///    by-value calls with Empty don't crash but return ERROR_INVALID_PARAMETER,
///    which originally led us to (incorrectly) conclude that Balanced
///    couldn't be set. Once the calling convention is right, Empty IS
///    accepted and switches the system to Balanced.
///
/// We use the modern PowerSetUserConfigured{AC,DC}PowerMode pair (Win10
/// 1809+) and set both sides on each click, so a single tray choice
/// applies regardless of plugged-in / on-battery state — matching the
/// unified mental model the tray menu offers. Users wanting per-state
/// granularity can use Windows Settings → Power & battery directly.
/// </summary>
internal static class PowerMode
{
    public enum Mode { Unknown, Efficient, Balanced, Performance }

    // Documented overlay GUIDs (stable since Win10 1709).
    private static readonly Guid GuidEfficient   = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid GuidBalanced    = Guid.Empty;  // 00000000-...
    private static readonly Guid GuidPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid guid);

    // `ref Guid` (pointer) — see class comment quirk #2. Required on ARM64.
    [DllImport("powrprof.dll")]
    private static extern uint PowerSetUserConfiguredACPowerMode(ref Guid guid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid guid);

    /// <summary>Reads the currently effective Windows Power mode (accounts
    /// for AC/DC state). Returns Unknown if the active overlay is something
    /// other than the three Microsoft-standard ones (e.g. an OEM custom
    /// overlay) — the menu still works in that case, the check marks just
    /// don't highlight any of our three.</summary>
    public static Mode Get()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out Guid g) != 0) return Mode.Unknown;
            if (g == GuidEfficient)   return Mode.Efficient;
            if (g == GuidBalanced)    return Mode.Balanced;
            if (g == GuidPerformance) return Mode.Performance;
            return Mode.Unknown;
        }
        catch { return Mode.Unknown; }
    }

    /// <summary>Sets the Windows Power mode for both AC (plugged in) and
    /// DC (on battery) sides, so the choice persists across power state
    /// changes. Returns true only if both sides set successfully.</summary>
    public static bool Set(Mode mode)
    {
        var g = ModeToGuid(mode);
        if (g == null) return false;
        try
        {
            // ref needs an addressable local — and a fresh one for each
            // call, since the function may write back (defensive).
            var ac = g.Value;
            var dc = g.Value;
            var rcAc = PowerSetUserConfiguredACPowerMode(ref ac);
            var rcDc = PowerSetUserConfiguredDCPowerMode(ref dc);
            return rcAc == 0 && rcDc == 0;
        }
        catch { return false; }
    }

    /// <summary>True if the Power-mode overlay API itself works on this
    /// system (Win10 1809+ on most SKUs). Checked by attempting a GET.</summary>
    public static bool IsSupported()
    {
        try { return PowerGetEffectiveOverlayScheme(out _) == 0; }
        catch { return false; }
    }

    private static Guid? ModeToGuid(Mode m) => m switch
    {
        Mode.Efficient   => GuidEfficient,
        Mode.Balanced    => GuidBalanced,
        Mode.Performance => GuidPerformance,
        _ => null
    };

    public static string Label(Mode m) => m switch
    {
        Mode.Efficient   => "Best power efficiency",
        Mode.Balanced    => "Balanced",
        Mode.Performance => "Best performance",
        _                => "Unknown"
    };
}
