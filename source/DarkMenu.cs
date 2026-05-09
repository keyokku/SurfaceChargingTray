using System.Drawing;
using System.Windows.Forms;

namespace SurfaceChargingTray;

/// <summary>
/// Dark-mode renderer for the tray context menu. WinForms popup menus do
/// not pick up the system dark theme automatically, so we paint them
/// explicitly. Applied via <c>ContextMenuStrip.Renderer</c>.
/// </summary>
internal class DarkColorTable : ProfessionalColorTable
{
    private static readonly Color Bg          = Color.FromArgb(0x1F, 0x1F, 0x1F);
    private static readonly Color Hover       = Color.FromArgb(0x3E, 0x3E, 0x3E);
    private static readonly Color Border      = Color.FromArgb(0x55, 0x55, 0x55);
    private static readonly Color Accent      = Color.FromArgb(0x4D, 0xA6, 0xFF);

    public override Color MenuItemSelected                 => Hover;
    public override Color MenuItemBorder                   => Border;
    public override Color MenuItemSelectedGradientBegin    => Hover;
    public override Color MenuItemSelectedGradientEnd      => Hover;
    public override Color MenuItemPressedGradientBegin     => Hover;
    public override Color MenuItemPressedGradientEnd       => Hover;
    public override Color MenuItemPressedGradientMiddle    => Hover;
    public override Color MenuStripGradientBegin           => Bg;
    public override Color MenuStripGradientEnd             => Bg;
    public override Color ImageMarginGradientBegin         => Bg;
    public override Color ImageMarginGradientMiddle        => Bg;
    public override Color ImageMarginGradientEnd           => Bg;
    public override Color ToolStripDropDownBackground      => Bg;
    public override Color CheckBackground                  => Accent;
    public override Color CheckPressedBackground           => Accent;
    public override Color CheckSelectedBackground          => Accent;
    public override Color SeparatorDark                    => Border;
    public override Color SeparatorLight                   => Border;
    public override Color MenuBorder                       => Border;
    public override Color ToolStripBorder                  => Border;
    public override Color ButtonSelectedHighlight          => Hover;
    public override Color ButtonSelectedHighlightBorder    => Border;
}

internal class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // White text for normal items; gray for disabled.
        e.TextColor = e.Item.Enabled ? Color.White : Color.FromArgb(0x80, 0x80, 0x80);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Color.White;
        base.OnRenderArrow(e);
    }
}

internal static class DarkMenu
{
    // Renderers are stateless and reusable — instantiate once and assign
    // by reference instead of allocating a new renderer + color table on
    // every theme tick.
    private static readonly DarkMenuRenderer              DarkRenderer  = new();
    private static readonly ToolStripProfessionalRenderer LightRenderer = new();

    /// <summary>Apply the appropriate renderer to a ContextMenuStrip based on Windows apps theme.</summary>
    public static void ApplyTo(ContextMenuStrip menu)
    {
        if (DarkMode.IsAppsDarkMode())
        {
            menu.Renderer  = DarkRenderer;
            menu.BackColor = Color.FromArgb(0x1F, 0x1F, 0x1F);
            menu.ForeColor = Color.White;
        }
        else
        {
            menu.Renderer  = LightRenderer;
            menu.BackColor = SystemColors.Menu;
            menu.ForeColor = SystemColors.MenuText;
        }
    }
}
