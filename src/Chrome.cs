using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DownloadSearchAudio
{
    // Custom window chrome, adapted from YouTubeDownloader-Net10 src\DarkTitleBar.cs.
    // Borderless form + WS_THICKFRAME/WS_CAPTION restored + WM_NCCALCSIZE removal of
    // the non-client frame + WM_NCHITTEST for drag/resize. Extended with a two-line
    // title (app name + subtitle) and DWM rounded corners (Windows 11).
    internal static class ChromeApi
    {
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(ChromePoint pt, uint flags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        public static int GetDpiForWindowAt(Point pt)
        {
            IntPtr mon = MonitorFromPoint(new ChromePoint { X = pt.X, Y = pt.Y }, MonitorDefaultToNearest);
            if (mon != IntPtr.Zero)
            {
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return (int)dx;
            }
            return 96;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public const int SmCxSizeFrame = 32;
        public const int SmCySizeFrame = 33;
        public const int SmCxPaddedBorder = 92;

        public const int WmNcCalcSize = 0x83;
        public const int WmNcHitTest = 0x84;
        public const int WmGetMinMaxInfo = 0x24;
        public const int WmClipboardUpdate = 0x031D;
        public const int WmDpiChanged = 0x02E0;

        public const int HtClient = 1;
        public const int HtCaption = 2;
        public const int HtLeft = 10;
        public const int HtRight = 11;
        public const int HtTop = 12;
        public const int HtTopLeft = 13;
        public const int HtTopRight = 14;
        public const int HtBottom = 15;
        public const int HtBottomLeft = 16;
        public const int HtBottomRight = 17;
        public const int HtTransparent = -1;

        public const int WsMaximize = 0x01000000;
        public const int GwlStyle = -16;
        public const uint MonitorDefaultToNearest = 2;

        // DWMWA_WINDOW_CORNER_PREFERENCE / DWMWCP_ROUND
        public const int DwmWindowCornerPreference = 33;
        public const int DwmWcpRound = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct ChromePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ChromeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public uint CbSize;
            public ChromeRect Monitor;
            public ChromeRect Work;
            public uint Flags;
        }

        public static bool IsZoomedStyle(IntPtr hwnd)
        {
            return (GetWindowLong(hwnd, GwlStyle) & WsMaximize) != 0;
        }

        public static void ApplyRoundedCorners(IntPtr hwnd)
        {
            try
            {
                int pref = DwmWcpRound;
                DwmSetWindowAttribute(hwnd, DwmWcpRound, ref pref, sizeof(int));
            }
            catch { }
        }

        // Re-adds the standard window styles after FormBorderStyle.None so the OS
        // still provides animations/snap, while WM_NCCALCSIZE (handled by the
        // form) removes the visible non-client frame. Shared by the main window
        // and dialogs; a window that skips this shows a system-drawn strip.
        public static void ApplyChromeStyles(IntPtr hwnd)
        {
            int style = GetWindowLong(hwnd, GwlStyle);
            int desired = style | 0x00040000 | 0x00C00000 | 0x00020000 | 0x00010000 | 0x00080000;
            if (desired != style)
            {
                SetWindowLong(hwnd, GwlStyle, desired);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
            }
        }
    }

    public class ChromePanel : Panel
    {
        private readonly Form _form;

        public ChromePanel(Form form)
        {
            _form = form;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ChromeApi.WmNcHitTest && _form.WindowState != FormWindowState.Maximized)
            {
                int sx = unchecked((short)((long)m.LParam & 0xFFFF));
                int sy = unchecked((short)(((long)m.LParam >> 16) & 0xFFFF));
                Rectangle wr = _form.RectangleToScreen(_form.ClientRectangle);
                int fw = ChromeApi.GetSystemMetrics(ChromeApi.SmCxSizeFrame) + ChromeApi.GetSystemMetrics(ChromeApi.SmCxPaddedBorder);
                int fh = ChromeApi.GetSystemMetrics(ChromeApi.SmCySizeFrame) + ChromeApi.GetSystemMetrics(ChromeApi.SmCxPaddedBorder);
                bool edge = sx < wr.Left + fw || sx >= wr.Right - fw || sy < wr.Top + fh || sy >= wr.Bottom - fh;
                if (edge)
                {
                    m.Result = (IntPtr)ChromeApi.HtTransparent;
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }

    public class DarkTitleBar : Panel
    {
        public const int BarHeight = 36;
        public const int FrameWidth = 8;

        private readonly Form _form;
        private readonly Button _minBtn;
        private readonly Button _maxBtn;
        private readonly Button _closeBtn;
        private readonly Font _glyphFont = new Font("Segoe MDL2 Assets", 9F);
        private readonly Font _titleFont = new Font("Segoe UI", 12.5F, FontStyle.Bold);
        private string _subtitle = "";
        private const string GlyphMin = "\uE921";
        private const string GlyphMax = "\uE922";
        private const string GlyphRestore = "\uE923";
        private const string GlyphClose = "\uE8BB";

        // Real application icon (embedded in the EXE); drawn in the title bar
        // and used for the taskbar via Form.Icon. Null draws nothing.
        public Icon AppIcon { get; set; }

        public DarkTitleBar(Form form)
        {
            _form = form;
            BackColor = Theme.HeaderBar;
            DoubleBuffered = true;

            _minBtn = MakeButton(GlyphMin, "Minimize", delegate { _form.WindowState = FormWindowState.Minimized; });
            _maxBtn = MakeButton(GlyphMax, "Maximize", delegate
            {
                if (_form.WindowState == FormWindowState.Maximized) _form.WindowState = FormWindowState.Normal;
                else _form.WindowState = FormWindowState.Maximized;
            });
            _closeBtn = MakeButton(GlyphClose, "Close", delegate { _form.Close(); });
            _closeBtn.MouseEnter += delegate { _closeBtn.BackColor = Color.FromArgb(196, 43, 28); };
            _closeBtn.MouseLeave += delegate { _closeBtn.BackColor = Theme.HeaderBar; };
            Controls.Add(_minBtn);
            Controls.Add(_maxBtn);
            Controls.Add(_closeBtn);
            _minBtn.Visible = form.MinimizeBox;
            _maxBtn.Visible = form.MaximizeBox;
            PositionButtons();

            Dock = DockStyle.Top;
            Height = BarHeight;

            form.SizeChanged += delegate { UpdateMaxGlyph(); };
            form.TextChanged += delegate { Invalidate(); };
        }

        public string Subtitle
        {
            get { return _subtitle; }
            set { _subtitle = value ?? ""; Invalidate(); }
        }        private Button MakeButton(string glyph, string accName, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = glyph;
            b.Font = _glyphFont;
            b.AccessibleName = accName;
            b.Size = new Size(46, BarHeight);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Theme.HeaderBar;
            b.ForeColor = Theme.Light;
            b.TabStop = false;
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            b.MouseEnter += delegate { if (b != _closeBtn) b.BackColor = Theme.ButtonHover; };
            b.MouseLeave += delegate { if (b != _closeBtn) b.BackColor = Theme.HeaderBar; };
            b.Click += onClick;
            return b;
        }

        private void PositionButtons()
        {
            if (_closeBtn == null || _maxBtn == null || _minBtn == null) return;
            int next = Width;
            if (_closeBtn.Visible)
            {
                _closeBtn.Location = new Point(next - _closeBtn.Width, 0);
                next = _closeBtn.Left;
            }
            if (_maxBtn.Visible)
            {
                _maxBtn.Location = new Point(next - _maxBtn.Width, 0);
                next = _maxBtn.Left;
            }
            if (_minBtn.Visible)
            {
                _minBtn.Location = new Point(next - _minBtn.Width, 0);
            }
        }

        private void UpdateMaxGlyph()
        {
            if (_maxBtn == null) return;
            _maxBtn.Text = _form.WindowState == FormWindowState.Maximized ? GlyphRestore : GlyphMax;
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Real app icon (replaces the temporary drawn music-note chip).
            // 28px in the 36px bar = +40% over the original 20px, so the
            // green rim and the note stay legible; the bar height is kept.
            int iconSize = 28;
            int iconY = (Height - iconSize) / 2;
            if (AppIcon != null)
                e.Graphics.DrawIcon(AppIcon, new Rectangle(10, iconY, iconSize, iconSize));

            int textLeft = AppIcon != null ? 10 + iconSize + 8 : 12;
            int textRight = Width - 138;
            if (_minBtn != null && _minBtn.Visible) textRight = _minBtn.Left;
            else if (_maxBtn != null && _maxBtn.Visible) textRight = _maxBtn.Left;
            else if (_closeBtn != null && _closeBtn.Visible) textRight = _closeBtn.Left;

            TextRenderer.DrawText(e.Graphics, _form.Text, _titleFont,
                new Rectangle(textLeft, 0, Math.Max(1, textRight - textLeft), Height), Theme.Light,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ChromeApi.WmNcHitTest)
            {
                m.Result = (IntPtr)ChromeApi.HtTransparent;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_glyphFont != null) _glyphFont.Dispose();
                if (_titleFont != null) _titleFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
