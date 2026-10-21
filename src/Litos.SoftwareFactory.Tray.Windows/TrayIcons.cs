using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Litos.SoftwareFactory.Tray.Windows;

/// <summary>
/// The tray icon, drawn: the factory mark (a small board with three columns) and a corner that
/// says the state, as on the proposal page: an amber count of tasks awaiting you, a muted speaker,
/// a grey dash while reconnecting, a red "!" when the token was refused. Drawn at the size the
/// taskbar asks for, so it is sharp at every display scale. Icons are cached: there are few.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    public static readonly Color Petrol = Color.FromArgb(0x1D, 0x5C, 0x73);
    public static readonly Color Amber = Color.FromArgb(0xB7, 0x70, 0x0F);
    public static readonly Color Idle = Color.FromArgb(0x7D, 0x8A, 0x97);
    public static readonly Color Bad = Color.FromArgb(0xB3, 0x26, 0x1E);

    private readonly Dictionary<(IconKind, int, int), Icon> _cache = [];

    public Icon For(IconState state, int size)
    {
        var count = state.Kind == IconKind.Awaiting ? Math.Min(state.Count, 10) : 0;
        if (_cache.TryGetValue((state.Kind, count, size), out var icon))
            return icon;

        using var bitmap = Draw(state, size);
        var handle = bitmap.GetHicon();
        try
        {
            icon = (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }

        _cache[(state.Kind, count, size)] = icon;
        return icon;
    }

    /// <summary>The icon as a bitmap, in a 20-unit square scaled to <paramref name="size"/> pixels.</summary>
    public static Bitmap Draw(IconState state, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 20f, size / 20f);

        using (var body = new SolidBrush(state.Kind == IconKind.Offline ? Idle : Petrol))
        using (var board = Rounded(new RectangleF(1, 2, 15, 14), 3))
            g.FillPath(body, board);
        using (var white = new SolidBrush(Color.White))
        {
            g.FillRectangle(white, 3.5f, 5, 2.6f, 8);
            using var dim = new SolidBrush(Color.FromArgb(205, Color.White));
            g.FillRectangle(dim, 7.2f, 5, 2.6f, 5.5f);
            using var dimmer = new SolidBrush(Color.FromArgb(165, Color.White));
            g.FillRectangle(dimmer, 10.9f, 5, 2.6f, 3.5f);
        }

        // The corner badge: as large as fits inside the square, so a count is legible at 16 px.
        const float cx = 13.8f, cy = 13.8f, r = 6f;
        var corner = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
        switch (state.Kind)
        {
            case IconKind.Awaiting:
            {
                using var fill = new SolidBrush(Amber);
                g.FillEllipse(fill, corner);
                var text = state.Count > 9 ? "9+" : state.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                using var font = new Font("Segoe UI", text.Length > 1 ? 7f : 9.5f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var ink = new SolidBrush(Color.White);
                g.DrawString(text, font, ink, new RectangleF(corner.X - 1, corner.Y + 0.4f, corner.Width + 2, corner.Height), format);
                break;
            }
            case IconKind.Muted:
            {
                var ink = Color.FromArgb(0x1B, 0x1F, 0x24);
                using var fill = new SolidBrush(Color.White);
                using var pen = new Pen(ink, 1f);
                g.FillEllipse(fill, corner);
                g.DrawEllipse(pen, corner);
                using var speaker = new SolidBrush(ink);
                g.FillPolygon(speaker, new PointF[]
                {
                    new(cx - 3.2f, cy - 1.3f), new(cx - 1.6f, cy - 1.3f), new(cx + 0.6f, cy - 3.2f),
                    new(cx + 0.6f, cy + 3.2f), new(cx - 1.6f, cy + 1.3f), new(cx - 3.2f, cy + 1.3f),
                });
                using var slash = new Pen(ink, 1.3f);
                g.DrawLine(slash, cx - 3.6f, cy - 3.6f, cx + 3.6f, cy + 3.6f);
                break;
            }
            case IconKind.Offline:
            {
                using var fill = new SolidBrush(Color.White);
                using var pen = new Pen(Idle, 1f);
                g.FillEllipse(fill, corner);
                g.DrawEllipse(pen, corner);
                using var dash = new Pen(Idle, 2f);
                g.DrawLine(dash, cx - 3f, cy, cx + 3f, cy);
                break;
            }
            case IconKind.SignIn:
            {
                using var fill = new SolidBrush(Bad);
                g.FillEllipse(fill, corner);
                using var mark = new Pen(Color.White, 2f);
                g.DrawLine(mark, cx, cy - 3.6f, cx, cy + 0.8f);
                using var dot = new SolidBrush(Color.White);
                g.FillEllipse(dot, cx - 1.1f, cy + 2f, 2.2f, 2.2f);
                break;
            }
        }

        return bitmap;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        foreach (var icon in _cache.Values)
            icon.Dispose();
        _cache.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
