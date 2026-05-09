using System.Drawing;
using System.Reflection;

namespace SurfaceChargingTray;

internal static class Icons
{
    public static Icon Load(string logicalName)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' not found.");
        return new Icon(s);
    }

    public static Icon PlugWhite() => Load("plug-white.ico");
    public static Icon PlugBlack() => Load("plug-black.ico");
    public static Icon ErrorRed()  => Load("error-red.ico");
}
