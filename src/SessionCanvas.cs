using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ClaudeSessionDeck
{
    internal sealed class SessionRow
    {
        public Session Session;
        public char Mark; // '*' open, '?' pid unknown and tab not found, ' ' not open
        public string Status = string.Empty; // "busy" / "idle" as Claude Code reports it; empty when unknown
    }

    internal static class Theme
    {
        // The same monospace family the terminal uses. Cascadia Mono (Windows Terminal's default) is
        // bundled inside Windows Terminal and usually not installed system-wide, so Consolas is the fallback.
        public static readonly string Mono = PickMono();

        public static readonly Color Background = Color.FromArgb(0x14, 0x16, 0x1A);
        public static readonly Color Separator = Color.FromArgb(0x24, 0x27, 0x2D);
        public static readonly Color Hover = Color.FromArgb(0x1E, 0x21, 0x27);
        public static readonly Color Text = Color.FromArgb(0xE8, 0xEA, 0xED);
        public static readonly Color Muted = Color.FromArgb(0xA9, 0xB0, 0xB8);
        public static readonly Color Dim = Color.FromArgb(0x7D, 0x85, 0x90);
        public static readonly Color Open = Color.FromArgb(0x3D, 0xD6, 0x8C);
        public static readonly Color Busy = Color.FromArgb(0x38, 0xBD, 0xF8);
        public static readonly Color Warn = Color.FromArgb(0xF0, 0xB4, 0x29);
        public static readonly Color Closed = Color.FromArgb(0x4C, 0x53, 0x5C);

        private static string PickMono()
        {
            foreach (string candidate in new[] { "Cascadia Mono", "Cascadia Code", "Consolas" })
            {
                foreach (FontFamily family in FontFamily.Families)
                {
                    if (string.Equals(family.Name, candidate, StringComparison.OrdinalIgnoreCase)) { return candidate; }
                }
            }
            return FontFamily.GenericMonospace.Name;
        }
    }

    /// <summary>
    /// The whole floating window content, custom drawn: a header with the title and a hide button,
    /// one card per session, and a footer line for the last action.
    /// </summary>
    internal sealed class SessionCanvas : Control
    {
        private const int HeaderHeight = 46;
        private const int FooterHeight = 30;
        private const int RowHeight = 56;
        private const int RowGap = 2;
        private const int SideMargin = 8;

        private readonly Font titleFont = new Font(Theme.Mono, 11f, FontStyle.Bold);
        private readonly Font nameFont = new Font(Theme.Mono, 10.5f, FontStyle.Bold);
        private readonly Font smallFont = new Font(Theme.Mono, 9f);
        private readonly Font chipFont = new Font(Theme.Mono, 8.5f);
        private readonly float scale;
        private List<SessionRow> rows = new List<SessionRow>();
        private int hover = -1;
        private bool hoverHide;
        private bool hoverPin;
        private bool pinned;
        private int scroll;
        private string status = string.Empty;

        public event Action<SessionRow> RowClicked;
        public event Action<SessionRow, Point> RowMenuRequested;
        public event Action HideClicked;
        public event Action PinClicked;

        /// <summary>
        /// Tells the canvas which client points belong to the window frame (drag area, resize edges).
        /// The canvas covers the whole form, so without this the form never gets a chance to answer
        /// the hit test and the window could be neither dragged nor resized.
        /// </summary>
        public Func<Point, bool> IsFrameArea;

        protected override void WndProc(ref Message m)
        {
            const int hitTest = 0x0084;
            const int transparent = -1;
            if (m.Msg == hitTest && IsFrameArea != null)
            {
                long lParam = m.LParam.ToInt64();
                Point point = PointToClient(new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)));
                if (IsFrameArea(point))
                {
                    m.Result = (IntPtr)transparent; // let the parent form decide
                    return;
                }
            }
            base.WndProc(ref m);
        }

        public SessionCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            using (Graphics g = CreateGraphics()) { scale = g.DpiX / 96f; }
        }

        public string Status
        {
            get { return status; }
            set { status = value ?? string.Empty; Invalidate(); }
        }

        public void SetRows(List<SessionRow> value)
        {
            rows = value;
            ClampScroll();
            Invalidate();
        }

        public int Px(int value) { return (int)Math.Round(value * scale); }
        public int HeaderPx { get { return Px(HeaderHeight); } }

        private Rectangle HideButton
        {
            get { return new Rectangle(Width - Px(40), Px(9), Px(28), Px(28)); }
        }

        private Rectangle PinButton
        {
            get { return new Rectangle(Width - Px(72), Px(9), Px(28), Px(28)); }
        }

        /// <summary>A pinned list stays open instead of collapsing back to the bubble.</summary>
        public bool Pinned
        {
            get { return pinned; }
            set { pinned = value; Invalidate(); }
        }

        /// <summary>True when the point lies on something clickable in the header (so it must not drag the window).</summary>
        public bool IsOnHeaderButton(Point point)
        {
            return HideButton.Contains(point) || PinButton.Contains(point);
        }

        private Rectangle ListArea
        {
            get { return new Rectangle(0, Px(HeaderHeight), Width, Math.Max(0, Height - Px(HeaderHeight) - Px(FooterHeight))); }
        }

        private int Step { get { return Px(RowHeight + RowGap); } }

        private void ClampScroll()
        {
            int overflow = rows.Count * Step - ListArea.Height;
            scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, overflow)));
        }

        private int RowAt(Point point)
        {
            Rectangle area = ListArea;
            if (!area.Contains(point)) { return -1; }
            int index = (point.Y - area.Top + scroll) / Step;
            return index >= 0 && index < rows.Count ? index : -1;
        }

        private Rectangle RowRect(int index)
        {
            Rectangle area = ListArea;
            return new Rectangle(Px(SideMargin), area.Top + index * Step - scroll, Width - Px(SideMargin) * 2, Px(RowHeight));
        }

        // ---- painting ----

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Background);

            g.SetClip(ListArea);
            if (rows.Count == 0) { DrawEmptyState(g); }
            for (int i = 0; i < rows.Count; i++) { DrawRow(g, i); }
            g.ResetClip();

            DrawHeader(g);
            DrawFooter(g);
            using (Pen border = new Pen(Theme.Separator)) { g.DrawRectangle(border, 0, 0, Width - 1, Height - 1); }
        }

        private void DrawHeader(Graphics g)
        {
            Rectangle header = new Rectangle(0, 0, Width, Px(HeaderHeight));
            TextRenderer.DrawText(g, "Claude sessions", titleFont, new Point(Px(16), Px(12)), Theme.Text, Theme.Background,
                TextFormatFlags.NoPadding);

            string count = rows.Count.ToString();
            Size titleSize = TextRenderer.MeasureText(g, "Claude sessions", titleFont, Size.Empty, TextFormatFlags.NoPadding);
            Rectangle pill = new Rectangle(Px(16) + titleSize.Width + Px(10), Px(14), Px(8) + TextRenderer.MeasureText(count, chipFont).Width, Px(18));
            FillRound(g, Theme.Hover, pill, pill.Height / 2);
            TextRenderer.DrawText(g, count, chipFont, pill, Theme.Muted, Theme.Hover,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            Rectangle button = HideButton;
            if (hoverHide) { FillRound(g, Theme.Hover, button, Px(6)); }
            using (Pen line = new Pen(hoverHide ? Theme.Text : Theme.Dim, Math.Max(1.5f, scale * 1.5f)))
            {
                int y = button.Top + button.Height / 2;
                g.DrawLine(line, button.Left + Px(8), y, button.Right - Px(8), y);
            }
            DrawPin(g, PinButton);
            using (Pen separator = new Pen(Theme.Separator)) { g.DrawLine(separator, 0, header.Bottom - 1, Width, header.Bottom - 1); }
        }

        // A tilted push-pin: outlined when free, solid and green when pinned.
        private void DrawPin(Graphics g, Rectangle button)
        {
            if (hoverPin) { FillRound(g, Theme.Hover, button, Px(6)); }
            Color color = pinned ? Theme.Open : (hoverPin ? Theme.Text : Theme.Dim);
            float unit = scale;
            GraphicsState saved = g.Save();
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TranslateTransform(button.Left + button.Width / 2f, button.Top + button.Height / 2f);
            g.RotateTransform(45f);
            using (Pen pen = new Pen(color, Math.Max(1.5f, unit * 1.5f)))
            using (Brush brush = new SolidBrush(color))
            {
                RectangleF head = new RectangleF(-3f * unit, -8f * unit, 6f * unit, 8f * unit);
                if (pinned) { g.FillRectangle(brush, head); } else { g.DrawRectangle(pen, head.X, head.Y, head.Width, head.Height); }
                g.DrawLine(pen, -5.5f * unit, 0f, 5.5f * unit, 0f);
                g.DrawLine(pen, 0f, 0f, 0f, 7f * unit);
            }
            g.Restore(saved);
        }

        private void DrawFooter(Graphics g)
        {
            int top = Height - Px(FooterHeight);
            using (Pen separator = new Pen(Theme.Separator)) { g.DrawLine(separator, 0, top, Width, top); }
            string text = status.Length > 0 ? status : "Click a session to switch  -  right-click for more";
            TextRenderer.DrawText(g, text, smallFont, new Rectangle(Px(16), top + 1, Width - Px(44), Px(FooterHeight) - 2), Theme.Dim,
                Theme.Background, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // resize grip
            using (Brush dot = new SolidBrush(Theme.Closed))
            {
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j <= i; j++)
                    {
                        int x = Width - Px(8) - j * Px(4);
                        int y = Height - Px(8) - (2 - i) * Px(4);
                        g.FillRectangle(dot, x, y, Px(2), Px(2));
                    }
                }
            }
        }

        private void DrawEmptyState(Graphics g)
        {
            TextRenderer.DrawText(g, "No sessions yet\nStart claude in a terminal and it shows up here.", smallFont, ListArea,
                Theme.Dim, Theme.Background, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void DrawRow(Graphics g, int index)
        {
            Rectangle row = RowRect(index);
            if (row.Bottom < ListArea.Top || row.Top > ListArea.Bottom) { return; }

            SessionRow item = rows[index];
            Color backdrop = index == hover ? Theme.Hover : Theme.Background;
            if (index == hover) { FillRound(g, Theme.Hover, row, Px(8)); }

            DrawStatusDot(g, row, item.Mark, item.Status);

            bool open = item.Mark != ' ';
            TextRenderer.DrawText(g, item.Session.name, nameFont, new Rectangle(row.Left + Px(38), row.Top + Px(9), row.Width - Px(100), Px(20)),
                open ? Theme.Text : Theme.Muted, backdrop,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, ShortPath(item.Session.dir), smallFont, new Rectangle(row.Left + Px(38), row.Top + Px(31), row.Width - Px(50), Px(16)),
                Theme.Dim, backdrop,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPadding);

            bool busy = item.Mark == '*' && item.Status == "busy";
            string chip = item.Mark == '*' ? (busy ? "busy" : (item.Status == "idle" ? "idle" : "open")) : (item.Mark == '?' ? "tab?" : "closed");
            Color chipColor = busy ? Theme.Busy : (item.Mark == '*' ? Theme.Open : (item.Mark == '?' ? Theme.Warn : Theme.Dim));
            TextRenderer.DrawText(g, chip, chipFont, new Rectangle(row.Right - Px(58), row.Top + Px(9), Px(50), Px(20)), chipColor, backdrop,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawStatusDot(Graphics g, Rectangle row, char mark, string status)
        {
            int cx = row.Left + Px(19);
            int cy = row.Top + Px(RowHeight) / 2;
            if (mark == ' ')
            {
                int r = Px(4);
                using (Pen ring = new Pen(Theme.Closed, Math.Max(1.5f, scale * 1.5f))) { g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2); }
                return;
            }
            Color color = mark == '*' ? (status == "busy" ? Theme.Busy : Theme.Open) : Theme.Warn;
            int halo = Px(8);
            using (Brush glow = new SolidBrush(Color.FromArgb(46, color))) { g.FillEllipse(glow, cx - halo, cy - halo, halo * 2, halo * 2); }
            int core = Px(4);
            using (Brush fill = new SolidBrush(color)) { g.FillEllipse(fill, cx - core, cy - core, core * 2, core * 2); }
        }

        // Shows the user profile folder as "~" so long paths stay readable.
        private static string ShortPath(string path)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase)) { return "~" + path.Substring(home.Length); }
            return path;
        }

        private static void FillRound(Graphics g, Color color, Rectangle rect, int radius)
        {
            using (GraphicsPath path = new GraphicsPath())
            using (Brush brush = new SolidBrush(color))
            {
                int d = Math.Max(2, radius * 2);
                path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }

        // ---- mouse ----

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = RowAt(e.Location);
            bool overHide = HideButton.Contains(e.Location);
            bool overPin = PinButton.Contains(e.Location);
            if (index != hover || overHide != hoverHide || overPin != hoverPin)
            {
                hover = index;
                hoverHide = overHide;
                hoverPin = overPin;
                Cursor = (index >= 0 || overHide || overPin) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            hoverHide = false;
            hoverPin = false;
            Cursor = Cursors.Default;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll -= Math.Sign(e.Delta) * Step;
            ClampScroll();
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HideButton.Contains(e.Location))
            {
                if (HideClicked != null) { HideClicked(); }
            }
            else if (e.Button == MouseButtons.Left && PinButton.Contains(e.Location))
            {
                if (PinClicked != null) { PinClicked(); }
            }
            else
            {
                int index = RowAt(e.Location);
                if (index >= 0)
                {
                    if (e.Button == MouseButtons.Left && RowClicked != null) { RowClicked(rows[index]); }
                    if (e.Button == MouseButtons.Right && RowMenuRequested != null) { RowMenuRequested(rows[index], e.Location); }
                }
            }
            base.OnMouseUp(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { titleFont.Dispose(); nameFont.Dispose(); smallFont.Dispose(); chipFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
