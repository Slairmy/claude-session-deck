# Generates app.ico (16-256 px) and a preview sheet. Run after changing the design; build.ps1 embeds the result.
#   powershell -File icon\make-icon.ps1
# Same look as the floating bubble: dark disc, green-blue-violet gradient ring, and a terminal prompt (">_") inside.
$here = $PSScriptRoot
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class IconMaker
{
    const int Supersample = 8;

    // Draws the icon at size x size: rendered large, then averaged down block by block (GDI+ shrinking leaves stair-steps).
    public static Bitmap Render(int size)
    {
        int big = size * Supersample;
        using (Bitmap large = new Bitmap(big, big, PixelFormat.Format32bppPArgb))
        {
            using (Graphics g = Graphics.FromImage(large))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Draw(g, big, size);
            }
            return BoxDownscale(large, size);
        }
    }

    static void Draw(Graphics g, float s, int finalSize)
    {
        float stroke = s / 11f;                 // a little heavier than the bubble: icons are seen small
        float inset = s / 32f;
        RectangleF disc = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);
        RectangleF ring = RectangleF.Inflate(disc, -stroke / 2, -stroke / 2);

        using (Brush fill = new SolidBrush(Color.FromArgb(0x14, 0x16, 0x1A))) { g.FillEllipse(fill, disc); }
        using (LinearGradientBrush gradient = new LinearGradientBrush(new RectangleF(0, 0, s, s), Color.Empty, Color.Empty, 45f))
        {
            ColorBlend blend = new ColorBlend(3);
            blend.Colors = new Color[] { Color.FromArgb(0x3D, 0xD6, 0x8C), Color.FromArgb(0x38, 0xBD, 0xF8), Color.FromArgb(0xA7, 0x8B, 0xFA) };
            blend.Positions = new float[] { 0f, 0.5f, 1f };
            gradient.InterpolationColors = blend;
            using (Pen pen = new Pen(gradient, stroke)) { g.DrawEllipse(pen, ring); }
        }

        // ">_" : chevron plus cursor. The cursor is dropped at tiny sizes where it would only smudge.
        float line = s * (finalSize <= 24 ? 0.105f : 0.085f);
        using (Pen pen = new Pen(Color.FromArgb(0xE8, 0xEA, 0xED), line))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            bool cursor = finalSize > 24;
            float shift = cursor ? -0.045f : 0f;
            g.DrawLines(pen, new PointF[] {
                new PointF(s * (0.37f + shift), s * 0.34f),
                new PointF(s * (0.53f + shift), s * 0.50f),
                new PointF(s * (0.37f + shift), s * 0.66f) });
            if (cursor) { g.DrawLine(pen, s * (0.58f + shift), s * 0.66f, s * (0.70f + shift), s * 0.66f); }
        }
    }

    static Bitmap BoxDownscale(Bitmap large, int size)
    {
        int factor = Supersample;
        Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        BitmapData from = large.LockBits(new Rectangle(0, 0, large.Width, large.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        BitmapData to = result.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            byte[] source = new byte[from.Stride * from.Height];
            Marshal.Copy(from.Scan0, source, 0, source.Length);
            byte[] target = new byte[to.Stride * to.Height];
            int area = factor * factor;
            int[] sum = new int[4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Array.Clear(sum, 0, 4);
                    for (int dy = 0; dy < factor; dy++)
                    {
                        int row = (y * factor + dy) * from.Stride + x * factor * 4;
                        for (int dx = 0; dx < factor; dx++)
                            for (int c = 0; c < 4; c++) sum[c] += source[row + dx * 4 + c];
                    }
                    for (int c = 0; c < 4; c++) target[y * to.Stride + x * 4 + c] = (byte)((sum[c] + area / 2) / area);
                }
            Marshal.Copy(target, 0, to.Scan0, target.Length);
        }
        finally { large.UnlockBits(from); result.UnlockBits(to); }
        return result;
    }

    // ICO with one image per size: 256 px as PNG (what Windows expects), the rest as 32-bit DIBs (what every loader understands).
    public static void WriteIco(string path, int[] sizes)
    {
        List<byte[]> images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using (Bitmap bmp = Render(size)) { images.Add(size >= 256 ? Png(bmp) : Dib(bmp)); }
        }
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] image in images) w.Write(image);
        }
    }

    static byte[] Png(Bitmap bmp)
    {
        using (MemoryStream ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    // BITMAPINFOHEADER (height doubled for the AND mask), bottom-up straight-alpha BGRA rows, then an all-zero AND mask.
    static byte[] Dib(Bitmap bmp)
    {
        int n = bmp.Width;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(n * n * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = n - 1; y >= 0; y--)
                for (int x = 0; x < n; x++)
                {
                    Color c = bmp.GetPixel(x, y); // GetPixel returns straight (un-premultiplied) colour
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            int maskRow = ((n + 31) / 32) * 4;
            w.Write(new byte[maskRow * n]);
            return ms.ToArray();
        }
    }

    public static void Sheet(string path, int[] sizes)
    {
        int width = 20; foreach (int s in sizes) width += s + 20;
        using (Bitmap sheet = new Bitmap(width, 300))
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(0x2B, 0x2E, 0x34));
            int x = 20;
            foreach (int s in sizes)
            {
                using (Bitmap b = Render(s)) { g.DrawImage(b, x, 20, s, s); }
                x += s + 20;
            }
            // light background strip, since the icon also sits on light taskbars and title bars
            g.FillRectangle(Brushes.White, 0, 180, width, 120);
            x = 20;
            foreach (int s in sizes)
            {
                if (s <= 64) { using (Bitmap b = Render(s)) { g.DrawImage(b, x, 200, s, s); } }
                x += s + 20;
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }
}
'@

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
[IconMaker]::WriteIco("$here\app.ico", $sizes)
[IconMaker]::Sheet("$here\preview.png", [int[]](16, 24, 32, 48, 64, 128))
"wrote $here\app.ico ($((Get-Item "$here\app.ico").Length) bytes)"
