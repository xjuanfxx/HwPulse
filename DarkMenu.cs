using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HwPulse;

/// <summary>Menú de la bandeja con los mismos colores que la app (fondo #111111, bordes #262626).</summary>
internal static class DarkMenu
{
    public static readonly Color Back = Color.FromArgb(17, 17, 17);
    public static readonly Color Hover = Color.FromArgb(38, 38, 38);
    public static readonly Color Border = Color.FromArgb(48, 48, 48);
    public static readonly Color Text = Color.FromArgb(229, 229, 229);
    public static readonly Color Muted = Color.FromArgb(140, 140, 140);

    public static ContextMenuStrip Create()
    {
        var m = new ContextMenuStrip
        {
            Renderer = new Renderer(),
            BackColor = Back,
            ForeColor = Text,
            ShowImageMargin = false,
            Padding = new Padding(4),
            Font = new Font("Segoe UI", 9.5f),
        };
        return m;
    }

    public static ToolStripMenuItem Item(string text, string? shortcut, EventHandler onClick)
    {
        var it = new ToolStripMenuItem(text, null, onClick)
        {
            ForeColor = Text,
            ShortcutKeyDisplayString = shortcut,
            Padding = new Padding(6, 5, 6, 5),
        };
        return it;
    }

    private sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            using var b = new SolidBrush(e.Item.Selected ? Hover : Back);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(r, 4);
            e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // El atajo (texto a la derecha) va en gris
            bool isShortcut = e.Item is ToolStripMenuItem mi && e.Text == mi.ShortcutKeyDisplayString;
            e.TextColor = isShortcut ? Muted : Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var p = new Pen(Border);
            e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(Border);
            e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
