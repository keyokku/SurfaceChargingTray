using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace SurfaceChargingTray;

/// <summary>
/// Generates the tray icon variants programmatically by overlaying a small
/// colored corner badge on the existing plug icon. The plug stays clearly
/// recognizable as "Surface Charging Tray"; the badge indicates the
/// current charging mode at a glance.
///
/// Three badge variants per plug-base (light vs dark theme) = 6 cached
/// icons total. Each is drawn once on first request and kept for the
/// process lifetime (~2-3 KB each).
///
/// Badge palette tuned for visibility at 16x16 through 48x48 against both
/// light and dark Windows 11 taskbars:
///   - Adaptive (Smart Charging engaged): green circle + white waveform
///   - Limit to 80%:                       blue circle + white "80"
///   - Charge to 100%:                     orange circle + white "100"
///
/// The badge sits in the bottom-right corner at ~45% of the icon dimension
/// (smaller than the plug, distinct enough to be its own glyph) with a
/// thin white halo so it reads against any base color.
/// </summary>
internal static class IconBuilder
{
    public enum Badge { Adaptive, Limit80, Charge100 }

    // Material Design 700 weights — saturated enough not to wash out on
    // either light or dark taskbars, distinct from each other at thumb size.
    public static readonly Color ColorAdaptive  = Color.FromArgb(0x38, 0x8E, 0x3C); // Green 700
    public static readonly Color ColorLimit80   = Color.FromArgb(0x19, 0x76, 0xD2); // Blue  700
    public static readonly Color ColorCharge100 = Color.FromArgb(0xF5, 0x7C, 0x00); // Orange 700

    // Cached generated icons. Built once on first request, kept for the
    // process lifetime. Keyed by (badge, darkBase) — darkBase = true means
    // the underlying plug is the white-on-dark variant (system tray is dark).
    private static readonly Dictionary<(Badge, bool), Icon> _cache = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Returns the tray-icon variant for the given mode + theme. Caches
    /// the result; safe to call from any thread.
    /// </summary>
    public static Icon Get(Badge badge, bool darkBase)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue((badge, darkBase), out var cached)) return cached;
            var ico = BuildBadgedIcon(badge, darkBase);
            _cache[(badge, darkBase)] = ico;
            return ico;
        }
    }

    private static Icon BuildBadgedIcon(Badge badge, bool darkBase)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48 };
        var frames = new List<Bitmap>(sizes.Length);
        using var baseIco = darkBase ? Icons.PlugWhite() : Icons.PlugBlack();
        foreach (var s in sizes)
            frames.Add(DrawBadgedFrame(s, baseIco, badge));
        return BundleIco(frames);
    }

    private static Bitmap DrawBadgedFrame(int size, Icon baseIco, Badge badge)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;

        // 1. Draw the base plug icon scaled to this size.
        g.DrawIcon(baseIco, new Rectangle(0, 0, size, size));

        // 2. Color-only badge in the bottom-right corner. ~33% of icon
        // dimension — visible at tray size without overpowering the plug.
        // Slight inset from the corner so anti-aliasing doesn't bleed past
        // the icon edge.
        int badgeSize = Math.Max(5, (int)Math.Round(size * 0.33f));
        int inset     = Math.Max(1, (int)Math.Round(size * 0.04f));
        int badgeX    = size - badgeSize - inset;
        int badgeY    = size - badgeSize - inset;

        var badgeColor = badge switch
        {
            Badge.Adaptive  => ColorAdaptive,
            Badge.Limit80   => ColorLimit80,
            Badge.Charge100 => ColorCharge100,
            _               => Color.Gray
        };
        using (var brush = new SolidBrush(badgeColor))
            g.FillEllipse(brush, badgeX, badgeY, badgeSize, badgeSize);

        return bmp;
    }

    /// <summary>
    /// Bundles multiple bitmap frames into a multi-resolution ICO and
    /// returns it as a System.Drawing.Icon. Standard ICO file format
    /// with PNG-encoded image data per frame (Windows supports
    /// PNG-in-ICO from Vista onward, gives us 32bpp with alpha).
    /// </summary>
    private static Icon BundleIco(List<Bitmap> frames)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var pngs = new List<byte[]>(frames.Count);
        foreach (var bmp in frames)
        {
            using var fms = new MemoryStream();
            bmp.Save(fms, ImageFormat.Png);
            pngs.Add(fms.ToArray());
        }

        // ICONDIR header
        bw.Write((ushort)0);            // reserved
        bw.Write((ushort)1);            // type: 1 = ICO
        bw.Write((ushort)frames.Count); // image count

        int dirSize = 6 + 16 * frames.Count;
        int offset  = dirSize;
        for (int i = 0; i < frames.Count; i++)
        {
            int w = frames[i].Width;
            int h = frames[i].Height;
            bw.Write((byte)(w >= 256 ? 0 : w));
            bw.Write((byte)(h >= 256 ? 0 : h));
            bw.Write((byte)0);                // palette
            bw.Write((byte)0);                // reserved
            bw.Write((ushort)1);              // color planes
            bw.Write((ushort)32);             // bpp
            bw.Write((uint)pngs[i].Length);
            bw.Write((uint)offset);
            offset += pngs[i].Length;
        }
        foreach (var data in pngs) bw.Write(data);

        ms.Position = 0;
        var ico = new Icon(ms);
        foreach (var bmp in frames) bmp.Dispose();
        return ico;
    }
}
