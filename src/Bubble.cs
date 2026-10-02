using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ClaudeSessionDeck
{
    /// <summary>
    /// The round bubble as a layered window with per-pixel alpha: everything outside the circle is truly transparent,
    /// so no square frame, shadow or jagged clip edge shows on any background.
    /// </summary>
    internal static class Bubble
    {
        private const int GwlExStyle = -20;
        private const long WsExLayered = 0x00080000;
        private const int UlwAlpha = 2;

        private static readonly Color[] OpenRing = { Color.FromArgb(0x3D, 0xD6, 0x8C), Color.FromArgb(0x38, 0xBD, 0xF8), Color.FromArgb(0xA7, 0x8B, 0xFA) };
        private static readonly Color[] WarnRing = { Color.FromArgb(0xF0, 0xB4, 0x29), Color.FromArgb(0xFB, 0x92, 0x3C), Color.FromArgb(0xF4, 0x72, 0xB6) };
        private static readonly Color[] ClosedRing = { Color.FromArgb(0x4C, 0x53, 0x5C), Color.FromArgb(0x7D, 0x85, 0x90), Color.FromArgb(0x4C, 0x53, 0x5C) };

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { public int Width, Height; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref NativePoint dstPoint, ref NativeSize size,
            IntPtr srcDc, ref NativePoint srcPoint, int colorKey, ref BlendFunction blend, int flags);
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ColorsUsed, ColorsImportant;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

        /// <summary>Turns the layered style on or off; a window that is not layered paints normally.</summary>
        public static void SetLayered(IntPtr hwnd, bool on)
        {
            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            long next = on ? (style | WsExLayered) : (style & ~WsExLayered);
            if (next != style) { SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(next)); }
        }

        /// <summary>Draws the bubble into the layered window; its position is left as it is.</summary>
        public static bool Apply(IntPtr hwnd, Point location, int size, int count, char state, Color background, Color textColor, string fontName)
        {
            bool ok = false;
            using (Bitmap image = Render(size, count, state, background, textColor, fontName))
            {
                IntPtr screen = GetDC(IntPtr.Zero);
                IntPtr memory = CreateCompatibleDC(screen);
                IntPtr hBitmap = ToDibSection(screen, image);
                IntPtr previous = SelectObject(memory, hBitmap);
                try
                {
                    NativeSize dimensions = new NativeSize { Width = size, Height = size };
                    NativePoint origin = new NativePoint();
                    NativePoint position = new NativePoint { X = location.X, Y = location.Y };
                    BlendFunction blend = new BlendFunction { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
                    ok = UpdateLayeredWindow(hwnd, IntPtr.Zero, ref position, ref dimensions, memory, ref origin, 0, ref blend, UlwAlpha);
                    if (!ok) { Hook.Log("bubble", "UpdateLayeredWindow failed, error " + Marshal.GetLastWin32Error() + " size=" + size + " pos=" + location + " hbmp=" + hBitmap + " prev=" + previous + " mem=" + memory + " hwnd=" + hwnd + " img=" + image.Size + " exstyle=" + GetWindowLongPtr(hwnd, GwlExStyle).ToInt64().ToString("X") + " style=" + GetWindowLongPtr(hwnd, -16).ToInt64().ToString("X")); }
                }
                finally
                {
                    SelectObject(memory, previous);
                    DeleteObject(hBitmap);
                    DeleteDC(memory);
                    ReleaseDC(IntPtr.Zero, screen);
                }
            }
            return ok;
        }

        // A 32-bit top-down DIB holding the image's premultiplied pixels, which is what UpdateLayeredWindow blends.
        private static IntPtr ToDibSection(IntPtr dc, Bitmap image)
        {
            BitmapInfoHeader info = new BitmapInfoHeader();
            info.Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader));
            info.Width = image.Width;
            info.Height = -image.Height;
            info.Planes = 1;
            info.BitCount = 32;
            IntPtr bits;
            IntPtr dib = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) { throw new InvalidOperationException("CreateDIBSection failed, error " + Marshal.GetLastWin32Error()); }

            System.Drawing.Imaging.BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                byte[] pixels = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                Marshal.Copy(pixels, 0, bits, pixels.Length);
            }
            finally
            {
                image.UnlockBits(data);
            }
            return dib;
        }

        // Drawn at several times the size and averaged down, which smooths the edges of the ring and the digits.
        private const int Supersample = 8;

        // The digits are only ~20 px tall: a proportional sans keeps the "3" open, where a monospace face closes it up.
        public const string DigitFont = "Segoe UI";

        // state: '*' something is open, '?' only sessions whose tab was not found, anything else: none open.
        private static Bitmap Render(int size, int count, char state, Color background, Color textColor, string fontName)
        {
            int big = size * Supersample;
            using (Bitmap large = new Bitmap(big, big, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(large))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    DrawBubble(g, big, count, state, background, textColor, fontName);
                }
                return BoxDownscale(large, size, Supersample);
            }
        }

        // Averages each factor x factor block of premultiplied pixels. GDI+ bicubic/bilinear shrinking only samples
        // a few source pixels per target pixel, which leaves stair-steps on thin curves such as the digits.
        private static Bitmap BoxDownscale(Bitmap large, int size, int factor)
        {
            Bitmap result = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            System.Drawing.Imaging.BitmapData from = large.LockBits(new Rectangle(0, 0, large.Width, large.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            System.Drawing.Imaging.BitmapData to = result.LockBits(new Rectangle(0, 0, size, size),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                byte[] source = new byte[from.Stride * from.Height];
                Marshal.Copy(from.Scan0, source, 0, source.Length);
                byte[] target = new byte[to.Stride * to.Height];
                int area = factor * factor;
                int[] sum = new int[4];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        Array.Clear(sum, 0, 4);
                        for (int dy = 0; dy < factor; dy++)
                        {
                            int row = (y * factor + dy) * from.Stride + x * factor * 4;
                            for (int dx = 0; dx < factor; dx++)
                            {
                                for (int c = 0; c < 4; c++) { sum[c] += source[row + dx * 4 + c]; }
                            }
                        }
                        int at = y * to.Stride + x * 4;
                        for (int c = 0; c < 4; c++) { target[at + c] = (byte)((sum[c] + area / 2) / area); }
                    }
                }
                Marshal.Copy(target, 0, to.Scan0, target.Length);
            }
            finally
            {
                large.UnlockBits(from);
                result.UnlockBits(to);
            }
            return result;
        }

        private static void DrawBubble(Graphics g, int size, int count, char state, Color background, Color textColor, string fontName)
        {
            Color[] ring = state == '*' ? OpenRing : (state == '?' ? WarnRing : ClosedRing);
            float stroke = size / 14f;
            float inset = size / 28f;
            RectangleF disc = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
            RectangleF area = RectangleF.Inflate(disc, -stroke / 2, -stroke / 2);

            using (Brush fill = new SolidBrush(background)) { g.FillEllipse(fill, disc); }
            using (LinearGradientBrush gradient = new LinearGradientBrush(new RectangleF(0, 0, size, size), ring[0], ring[2], 45f))
            {
                ColorBlend blend = new ColorBlend(3);
                blend.Colors = ring;
                blend.Positions = new float[] { 0f, 0.5f, 1f };
                gradient.InterpolationColors = blend;
                using (Pen pen = new Pen(gradient, stroke)) { g.DrawEllipse(pen, area); }
            }

            // Centre the digits by their drawn outline: text layout boxes carry empty space above and below the glyphs.
            using (GraphicsPath digits = new GraphicsPath())
            {
                digits.AddString(count.ToString(), Family(fontName), (int)FontStyle.Bold, size * 0.46f, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF ink = digits.GetBounds();
                using (Matrix centre = new Matrix())
                {
                    centre.Translate(size / 2f - (ink.X + ink.Width / 2f), size / 2f - (ink.Y + ink.Height / 2f));
                    digits.Transform(centre);
                }
                using (Brush text = new SolidBrush(textColor)) { g.FillPath(text, digits); }
            }
        }

        private static FontFamily Family(string name)
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { return FontFamily.GenericSansSerif; }
        }
    }
}
