using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeSessionDeck
{
    /// <summary>
    /// The floating session list: a small always-on-top window. It rests as a round bubble showing how many
    /// sessions are open; a click expands it into the list, and a click anywhere outside collapses it again.
    /// Click a session to bring its terminal window and tab to the front. The hide button sends it to the tray.
    /// </summary>
    internal sealed class OverlayForm : Form
    {
        private const string MutexName = "claude-session-deck-overlay";
        private const int StatusSeconds = 8;
        private const int BubbleSize = 56;
        private const int DragThreshold = 4;

        private readonly SessionCanvas canvas = new SessionCanvas();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly ContextMenuStrip rowMenu = new ContextMenuStrip();
        private SessionRow menuRow;
        private DateTime statusAt = DateTime.MinValue;
        private bool refreshing;
        private bool quitting;
        private bool savePending;
        private System.Threading.Timer saveDebounce;

        // bubble / list state
        private bool collapsed;
        private bool pinned;           // keep the list open instead of collapsing to the bubble
        private bool switching;        // a bounds change we made ourselves, not the user's
        private bool suppressCollapse; // a menu or dialog of ours is open and steals the focus
        private Rectangle listBounds;
        private Point bubbleLocation;
        private List<SessionRow> lastRows = new List<SessionRow>();
        private string bubbleKey = string.Empty;
        private ContextMenuStrip trayMenu;
        private bool pressed;
        private bool dragged;
        private Point dragStartCursor;
        private Point dragStartLocation;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private static string BoundsPath { get { return Path.Combine(Store.Root, "overlay.json"); } }

        /// <summary>Starts the overlay as a separate process unless one is already running.</summary>
        public static void EnsureRunning()
        {
            if (Environment.GetEnvironmentVariable("DECK_NO_OVERLAY") != null) { return; }
            Mutex existing;
            if (Mutex.TryOpenExisting(MutexName, out existing)) { existing.Dispose(); return; }

            // Detached with no inherited handles: Claude Code waits for the hook's stdout pipe to close,
            // and a long-lived child holding it open would stall the session start.
            string gui = Path.Combine(Store.Root, "claude-session-deck-gui.exe");
            if (Native.IsProgramFile(gui))
            {
                Native.StartDetached(gui, string.Empty);
                return;
            }

            // A corrupted or encrypted claude-session-deck-gui.exe (seen with corporate file-encryption software) cannot start. claude-session-deck.exe runs the same window.
            Hook.Log("overlay", "claude-session-deck-gui.exe is not a valid program file; starting the window from claude-session-deck.exe instead");
            Native.StartDetached(Path.Combine(Store.Root, "claude-session-deck.exe"), " overlay");
        }

        public static void Run()
        {
            bool created;
            using (Mutex single = new Mutex(true, MutexName, out created))
            {
                if (!created) { return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new OverlayForm());
                GC.KeepAlive(single);
            }
        }

        private OverlayForm()
        {
            Text = "Claude sessions";
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Theme.Background;
            Icon = AppIcon.Load(SystemInformation.IconSize);
            DoubleBuffered = true;
            LoadBounds();

            canvas.Dock = DockStyle.Fill;
            canvas.IsFrameArea = delegate (Point p) { return HitCode(p) != 1; };
            canvas.RowClicked += OpenRow;
            canvas.RowMenuRequested += ShowRowMenu;
            canvas.HideClicked += Hide;
            canvas.PinClicked += TogglePin;
            Controls.Add(canvas);

            BuildRowMenu();
            BuildTray();
            canvas.Pinned = pinned;
            if (pinned) { ShowListAt(listBounds); } else { Collapse(); }

            timer.Interval = 1000;
            timer.Tick += delegate { OnTick(); };
            timer.Start();
            RefreshRows();
        }

        // Shown without stealing the keyboard focus from the terminal the user is typing in.
        protected override bool ShowWithoutActivation { get { return true; } }

        // Keeps the window out of Alt+Tab.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                // The bubble is a layered window; asking for it here keeps it across any re-created window handle.
                if (collapsed) { cp.ExStyle |= 0x00080000; } // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 11 look: rounded corners, dark title/frame colours, subtle border. Older Windows
            // versions reject these attributes, which is harmless.
            SetDwm(33, 2);          // DWMWA_WINDOW_CORNER_PREFERENCE = round
            SetDwm(20, 1);          // DWMWA_USE_IMMERSIVE_DARK_MODE
            SetDwm(34, 0x002D2724); // DWMWA_BORDER_COLOR (0x00BBGGRR)
            SetFrame(!collapsed);
            if (collapsed) { Bubble.SetLayered(Handle, true); }

            if (saveDebounce == null)
            {
                saveDebounce = new System.Threading.Timer(delegate
                {
                    savePending = false;
                    Post(delegate { SaveBounds(); });
                }, null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        // The list has the Windows 11 frame; the round bubble must not show the system border or shadow around its square.
        private void SetFrame(bool list)
        {
            if (!IsHandleCreated) { return; }
            SetDwm(34, list ? 0x002D2724 : unchecked((int)0xFFFFFFFE)); // DWMWA_BORDER_COLOR: none for the bubble
            SetDwm(33, list ? 2 : 1);                                   // DWMWA_WINDOW_CORNER_PREFERENCE: round / do not round
            SetDwm(2, list ? 0 : 1);                                    // DWMWA_NCRENDERING_POLICY: disabled drops the shadow
        }

        private void SetDwm(int attribute, int value)
        {
            DwmSetWindowAttribute(Handle, attribute, ref value, sizeof(int));
        }

        // A borderless window has no frame to grab, so answer the hit tests ourselves: drag by the
        // header, resize by the edges and the bottom-right corner.
        protected override void WndProc(ref Message m)
        {
            const int hitTest = 0x0084;
            if (m.Msg == hitTest && collapsed)
            {
                m.Result = (IntPtr)1; // HTCLIENT: the bubble handles its own click and drag
                return;
            }
            if (m.Msg == hitTest)
            {
                long lParam = m.LParam.ToInt64();
                Point point = PointToClient(new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)));
                m.Result = (IntPtr)HitCode(point);
                return;
            }
            base.WndProc(ref m);
        }

        private int HitCode(Point p)
        {
            const int client = 1, caption = 2, left = 10, right = 11, top = 12, bottom = 15, bottomLeft = 16, bottomRight = 17;
            int edge = canvas.Px(6);
            int grip = canvas.Px(18);
            if (p.X >= Width - grip && p.Y >= Height - grip) { return bottomRight; }
            if (p.X < edge && p.Y >= Height - grip) { return bottomLeft; }
            if (p.Y >= Height - edge) { return bottom; }
            if (p.X >= Width - edge) { return right; }
            if (p.X < edge) { return left; }
            if (p.Y < edge) { return top; }
            if (p.Y < canvas.HeaderPx && !canvas.IsOnHeaderButton(p)) { return caption; }
            return client;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (!collapsed && !switching) { listBounds = Bounds; }
            QueueSaveBounds();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!collapsed && !switching) { listBounds = Bounds; }
            QueueSaveBounds();
        }

        // ---- bubble <-> list ----

        private void Collapse()
        {
            if (collapsed) { return; }
            switching = true;
            collapsed = true;
            canvas.Visible = false;
            MinimumSize = Size.Empty;
            int size = canvas.Px(BubbleSize);
            Bounds = new Rectangle(bubbleLocation, new Size(size, size));
            ContextMenuStrip = trayMenu;
            SetFrame(false);
            if (IsHandleCreated) { Bubble.SetLayered(Handle, true); }
            switching = false;
            UpdateBubble(true);
        }

        private void Expand()
        {
            if (!collapsed) { return; }
            ShowListAt(ListBoundsNextToBubble());
            Activate();
            RefreshRows();
        }

        private void ShowListAt(Rectangle bounds)
        {
            switching = true;
            collapsed = false;
            if (IsHandleCreated) { Bubble.SetLayered(Handle, false); }
            ContextMenuStrip = null;
            MinimumSize = new Size(canvas.Px(264), canvas.Px(170));
            Bounds = bounds;
            canvas.Visible = true;
            SetFrame(true);
            switching = false;
        }

        private void TogglePin()
        {
            pinned = !pinned;
            canvas.Pinned = pinned;
            QueueSaveBounds();
        }

        // The list opens from the bubble's nearest corner, so it grows toward the middle of the screen.
        private Rectangle ListBoundsNextToBubble()
        {
            int size = canvas.Px(BubbleSize);
            Rectangle bubble = new Rectangle(bubbleLocation, new Size(size, size));
            Rectangle work = Screen.FromRectangle(bubble).WorkingArea;
            int width = Math.Min(listBounds.Width, work.Width), height = Math.Min(listBounds.Height, work.Height);
            bool rightHalf = bubble.Left + size / 2 > work.Left + work.Width / 2;
            bool lowerHalf = bubble.Top + size / 2 > work.Top + work.Height / 2;
            int x = rightHalf ? bubble.Right - width : bubble.Left;
            int y = lowerHalf ? bubble.Bottom - height : bubble.Top;
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            return new Rectangle(x, y, width, height);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateBubble(true);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!collapsed && !switching && !suppressCollapse && !pinned) { Collapse(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!collapsed || e.Button != MouseButtons.Left) { return; }
            pressed = true;
            dragged = false;
            dragStartCursor = Cursor.Position;
            dragStartLocation = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!collapsed || !pressed) { return; }
            Point cursor = Cursor.Position;
            int dx = cursor.X - dragStartCursor.X, dy = cursor.Y - dragStartCursor.Y;
            if (!dragged && Math.Abs(dx) <= DragThreshold && Math.Abs(dy) <= DragThreshold) { return; }
            dragged = true;
            Location = new Point(dragStartLocation.X + dx, dragStartLocation.Y + dy);
            bubbleLocation = Location;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!collapsed || !pressed) { return; }
            pressed = false;
            if (!dragged) { Expand(); }
        }

        // Ring colours, start to end of the diagonal gradient.
        // The bubble is a layered window drawn by Bubble; it is redrawn only when what it shows changes.
        private void UpdateBubble(bool force)
        {
            if (!collapsed || !IsHandleCreated || !Visible) { return; }
            int open = lastRows.Count(r => r.Mark == '*');
            char state = open > 0 ? '*' : (lastRows.Any(r => r.Mark == '?') ? '?' : ' ');
            string key = open + ":" + state;
            if (!force && key == bubbleKey) { return; }
            // Remembered only once drawn: an early attempt (before the window is really up) is retried on the next tick.
            if (Bubble.Apply(Handle, Location, canvas.Px(BubbleSize), open, state, Theme.Background, Theme.Text, Bubble.DigitFont)) { bubbleKey = key; }
        }

        // ---- menus ----

        private void BuildRowMenu()
        {
            StyleMenu(rowMenu);
            rowMenu.Items.Add("Open", null, delegate { if (menuRow != null) { OpenRow(menuRow); } });
            rowMenu.Items.Add("Rename...", null, delegate { if (menuRow != null) { RenameRow(menuRow); } });
            rowMenu.Items.Add("Unregister", null, delegate { if (menuRow != null) { Unregister(menuRow); } });
            rowMenu.Opened += delegate { suppressCollapse = true; };
            rowMenu.Closed += delegate { suppressCollapse = false; };
        }

        private void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            StyleMenu(menu);
            menu.Items.Add("Show / hide", null, delegate { ToggleVisible(); });
            menu.Items.Add("Quit", null, delegate { Quit(); });
            tray.Icon = AppIcon.Load(SystemInformation.SmallIconSize);
            tray.Text = "Claude sessions";
            tray.ContextMenuStrip = menu;
            trayMenu = menu;
            tray.Visible = true;
            tray.MouseClick += delegate (object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { ToggleVisible(); }
            };
        }

        private static void StyleMenu(ContextMenuStrip menu)
        {
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            menu.BackColor = Theme.Background;
            menu.ForeColor = Theme.Text;
            menu.Font = new Font(Theme.Mono, 9.5f);
            menu.ShowImageMargin = false;
        }

        private void ToggleVisible()
        {
            if (Visible) { Hide(); } else { Show(); }
        }

        private void Quit()
        {
            quitting = true;
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }

        private void ShowRowMenu(SessionRow row, Point location)
        {
            menuRow = row;
            rowMenu.Show(canvas, location);
        }

        // ---- data ----

        private void OnTick()
        {
            if (canvas.Status.Length > 0 && (DateTime.UtcNow - statusAt).TotalSeconds > StatusSeconds) { canvas.Status = string.Empty; }
            RefreshRows();
        }

        private void RefreshRows()
        {
            if (refreshing) { return; }
            refreshing = true;
            Task.Factory.StartNew(delegate
            {
                try { return BuildRows(); }
                catch (Exception ex)
                {
                    // The store can be mid-write when a hook updates it; try again on the next tick.
                    Hook.Log("overlay", "refresh skipped: " + ex.Message);
                    return null;
                }
            }).ContinueWith(delegate (Task<List<SessionRow>> task)
            {
                refreshing = false;
                if (task.Result != null)
                {
                    Post(delegate
                    {
                        lastRows = task.Result;
                        canvas.SetRows(task.Result);
                        UpdateBubble(false);
                    });
                }
            });
        }

        private static List<SessionRow> BuildRows()
        {
            List<SessionRow> rows = new List<SessionRow>();
            List<LiveClaude> live = ClaudeRegistry.ReadLive();
            // Sessions whose hook never registered them are picked up here, but only when they really run in a Windows Terminal window.
            Reconciler.Run(live, entry => ConsoleWindows.Of(entry.Pid) != 0);
            Reconciler.SyncWindows(live);
            foreach (Session session in Store.Load())
            {
                long window = Store.WindowOf(session.name);
                char mark = Tabs.MarkOf(window, session);
                LiveClaude entry = live.FirstOrDefault(e => e.Pid == session.pid) ?? live.FirstOrDefault(e => e.SessionId == session.id);
                rows.Add(new SessionRow { Session = session, Mark = mark, Status = entry == null ? string.Empty : entry.Status });
            }
            return rows;
        }

        // ---- actions ----

        private void OpenRow(SessionRow row)
        {
            Session session = row.Session;
            SetStatus(session.name + ": ...");
            Task.Factory.StartNew(delegate { return SessionOpener.OpenSession(session); })
                .ContinueWith(delegate (Task<string> task)
                {
                    string text = task.IsFaulted ? "Error: " + task.Exception.GetBaseException().Message : task.Result;
                    Post(delegate { SetStatus(session.name + ": " + text); });
                });
        }

        private void RenameRow(SessionRow row)
        {
            Session session = row.Session;
            suppressCollapse = true;
            string requested;
            try { requested = PromptForm.Ask(this, "Rename session", session.name); }
            finally { suppressCollapse = false; }
            if (requested == null) { return; }

            SetStatus("Renaming " + session.name + " ...");
            Task.Factory.StartNew(delegate { return Rename.Run(session, requested); })
                .ContinueWith(delegate (Task<string> task)
                {
                    string text = task.IsFaulted ? "Error: " + task.Exception.GetBaseException().Message : task.Result;
                    Post(delegate { SetStatus(text); RefreshRows(); });
                });
        }

        private void Unregister(SessionRow row)
        {
            Session session = row.Session;
            Store.WithLock(delegate { Store.Save(Store.Load().Where(s => s.id != session.id).ToList()); });
            Store.ForgetWindow(session.name);
            SetStatus("Unregistered " + session.name);
            RefreshRows();
        }

        private void SetStatus(string text)
        {
            canvas.Status = text;
            statusAt = DateTime.UtcNow;
        }

        private void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated) { return; }
            BeginInvoke(action);
        }

        // ---- window position ----

        private void LoadBounds()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int width = canvas.Px(300), height = canvas.Px(380), margin = canvas.Px(20), bubble = canvas.Px(BubbleSize);
            listBounds = new Rectangle(work.Right - width - margin, work.Bottom - height - margin, width, height);
            bubbleLocation = new Point(work.Right - bubble - margin, work.Bottom - bubble - margin);
            try
            {
                if (File.Exists(BoundsPath))
                {
                    Dictionary<string, int> saved = new JavaScriptSerializer()
                        .Deserialize<Dictionary<string, int>>(File.ReadAllText(BoundsPath));
                    Rectangle list = new Rectangle(saved["x"], saved["y"], saved["w"], saved["h"]);
                    if (IsOnScreen(list)) { listBounds = list; }
                    int pin;
                    pinned = saved.TryGetValue("pin", out pin) && pin != 0;
                    int bx, by;
                    if (saved.TryGetValue("bx", out bx) && saved.TryGetValue("by", out by)
                        && IsOnScreen(new Rectangle(bx, by, bubble, bubble)))
                    {
                        bubbleLocation = new Point(bx, by);
                    }
                }
            }
            catch (Exception ex)
            {
                Hook.Log("overlay", "ignored saved window position: " + ex.Message);
            }
            StartPosition = FormStartPosition.Manual;
        }

        private static bool IsOnScreen(Rectangle area)
        {
            return Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(area));
        }

        private void QueueSaveBounds()
        {
            if (!IsHandleCreated || !Visible || switching || saveDebounce == null || savePending) { return; }
            savePending = true;
            saveDebounce.Change(500, Timeout.Infinite);
        }

        private void SaveBounds()
        {
            Dictionary<string, int> saved = new Dictionary<string, int>();
            saved["x"] = listBounds.X; saved["y"] = listBounds.Y; saved["w"] = listBounds.Width; saved["h"] = listBounds.Height;
            saved["bx"] = bubbleLocation.X; saved["by"] = bubbleLocation.Y; saved["pin"] = pinned ? 1 : 0;
            File.WriteAllText(BoundsPath, new JavaScriptSerializer().Serialize(saved));
        }
    }

    /// <summary>Dark colours for the context menus.</summary>
    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Background; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Background; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Background; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Background; } }
        public override Color MenuBorder { get { return Theme.Separator; } }
        public override Color MenuItemBorder { get { return Theme.Hover; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color SeparatorDark { get { return Theme.Separator; } }
    }

    internal static class Overlay
    {
        public static void Run()
        {
            Thread ui = new Thread(OverlayForm.Run);
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            ui.Join();
        }

        public static void EnsureRunning()
        {
            OverlayForm.EnsureRunning();
        }
    }
}
