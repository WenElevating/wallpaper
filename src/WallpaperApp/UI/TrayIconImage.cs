using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace WallpaperApp.UI;

// Generates the default tray icon entirely in code — a dark "screen" with a play
// triangle in the app's Catppuccin palette — so the app ships with a real icon
// and no binary asset. It packs 16/24/32/48px PNGs into one multi-resolution ICO
// (PNG-in-ICO, supported since Vista) and returns a System.Drawing.Icon whose
// handle feeds Shell_NotifyIcon.
internal static class TrayIconImage
{
    public static Icon CreateDefault()
    {
        var sizes = new[] { 16, 24, 32, 48 };
        var pngs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            pngs[i] = RenderPng(sizes[i]);

        // ICO container: 6-byte header + one 16-byte directory entry per image,
        // followed by the PNG blobs themselves.
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0);              // reserved
        bw.Write((ushort)1);              // type = icon
        bw.Write((ushort)sizes.Length);   // image count

        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s)); // width  (0 == 256)
            bw.Write((byte)(s >= 256 ? 0 : s)); // height
            bw.Write((byte)0);                   // color count (0 == 8-bit+)
            bw.Write((byte)0);                   // reserved
            bw.Write((ushort)1);                 // planes
            bw.Write((ushort)32);                // bits per pixel
            bw.Write((uint)pngs[i].Length);      // image size in bytes
            bw.Write((uint)offset);              // image offset
            offset += pngs[i].Length;
        }
        foreach (var png in pngs)
            bw.Write(png);

        ms.Position = 0;
        return new Icon(ms);
    }

    private static byte[] RenderPng(int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        bmp.SetResolution(96, 96);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.FromArgb(0, 0, 0, 0));

            float pad = size * 0.10f;
            float rx = pad, ry = pad;
            float rw = size - 2 * pad, rh = size - 2 * pad;
            float radius = rh * 0.26f;

            // Rounded-rect "screen" with a dark vertical gradient.
            using var rectPath = RoundedRect(rx, ry, rw, rh, radius);
            using (var brush = new LinearGradientBrush(
                new RectangleF(rx, ry, rw, rh),
                Color.FromArgb(0x31, 0x32, 0x44), // Surface0
                Color.FromArgb(0x18, 0x18, 0x25), // Mantle
                LinearGradientMode.Vertical))
            {
                g.FillPath(brush, rectPath);
            }

            float borderW = Math.Max(1f, size / 24f);
            using var pen = new Pen(Color.FromArgb(0x58, 0x5B, 0x70), borderW); // Overlay0
            g.DrawPath(pen, rectPath);

            // Play triangle (Blue accent).
            float tx = rx + rw * 0.34f;
            float tw = rw * 0.32f;
            float ty = ry + rh * 0.30f;
            float th = rh * 0.40f;
            using var tri = new GraphicsPath();
            tri.AddLine(tx, ty, tx, ty + th);
            tri.AddLine(tx, ty + th, tx + tw, ry + rh * 0.5f);
            tri.CloseFigure();
            using var triBrush = new SolidBrush(Color.FromArgb(0x89, 0xB4, 0xFA));
            g.FillPath(triBrush, tri);
        }

        using var outs = new MemoryStream();
        bmp.Save(outs, ImageFormat.Png);
        return outs.ToArray();
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
