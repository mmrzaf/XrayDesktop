using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace XrayDesktop.UI;

// Notification-area icons are drawn at startup so the portable ZIP needs no image assets.
internal static class StatusIcons
{
    internal static readonly Icon Off = Create(Color.FromArgb(128, 128, 128));
    internal static readonly Icon Busy = Create(Color.FromArgb(230, 160, 20));
    internal static readonly Icon On = Create(Color.FromArgb(34, 160, 80));

    private static Icon Create(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(color);
            using var outline = new Pen(Color.FromArgb(110, 0, 0, 0), 2);
            graphics.FillEllipse(fill, 3, 3, 26, 26);
            graphics.DrawEllipse(outline, 3, 3, 26, 26);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            using var stream = new MemoryStream();
            temporary.Save(stream);
            stream.Position = 0;
            return new Icon(stream);
        }
        finally { _ = DestroyIcon(handle); }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
