using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DownloadSearchAudio
{
    // Small code-first UI building blocks (no Designer): rounded cards, rounded
    // buttons with optional MDL2 glyph, dark input boxes with inside icons,
    // slim progress bar, dark-themed ListView. WinForms has no built-in
    // rounded/dark equivalents, matching the mockup style.
    internal static class UiGraphics
    {
        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            // Handle invalid/zero-size rectangles (e.g., during minimize/restore)
            if (r.Width <= 0 || r.Height <= 0)
            {
                return p; // Return empty path - nothing to draw
            }
            if (radius <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath RoundedRect(RectangleF r, int radius)
        {
            return RoundedRect(Rectangle.Round(r), radius);
        }
    }

    // Loads the standard Windows small folder stock icon. A small drawing is
    // retained as a fallback for systems where the shell icon API is unavailable.
    internal static class FolderIconResource
    {
        private const uint StockIconFolder = 3;
        private const uint StockIconSmall = 0x00000001;
        private const uint StockIconIcon = 0x00000100;
        private static readonly Image _small = LoadSmallFolderIcon();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StockIconInfo
        {
            public uint Size;
            public IntPtr IconHandle;
            public int SystemImageIndex;
            public int IconIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string Path;
        }

        [DllImport("shell32.dll", PreserveSig = true)]
        private static extern int SHGetStockIconInfo(uint stockIcon, uint flags, ref StockIconInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr icon);

        public static Image Small { get { return _small; } }

        private static Image LoadSmallFolderIcon()
        {
            StockIconInfo info = new StockIconInfo();
            info.Size = (uint)Marshal.SizeOf(typeof(StockIconInfo));
            try
            {
                if (SHGetStockIconInfo(StockIconFolder, StockIconIcon | StockIconSmall, ref info) == 0
                    && info.IconHandle != IntPtr.Zero)
                {
                    try
                    {
                        Icon borrowed = Icon.FromHandle(info.IconHandle);
                        using (Icon copy = (Icon)borrowed.Clone())
                        using (Bitmap source = copy.ToBitmap())
                        {
                            Bitmap result = new Bitmap(16, 16);
                            using (Graphics graphics = Graphics.FromImage(result))
                            {
                                graphics.Clear(Color.Transparent);
                                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                graphics.DrawImage(source, new Rectangle(0, 0, 16, 16));
                            }
                            return result;
                        }
                    }
                    finally
                    {
                        DestroyIcon(info.IconHandle);
                    }
                }
            }
            catch
            {
                // The handle is destroyed by the inner finally block whenever
                // it was returned successfully.
            }

            return DrawFallback();
        }

        private static Image DrawFallback()
        {
            Bitmap image = new Bitmap(16, 16);
            using (Graphics graphics = Graphics.FromImage(image))
            using (SolidBrush back = new SolidBrush(Color.FromArgb(218, 157, 48)))
            using (SolidBrush front = new SolidBrush(Color.FromArgb(250, 199, 82)))
            using (Pen edge = new Pen(Color.FromArgb(176, 119, 30), 1f))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                Point[] folderBack = { new Point(1, 3), new Point(6, 3), new Point(8, 5), new Point(14, 5), new Point(15, 7), new Point(14, 13), new Point(1, 13) };
                graphics.FillPolygon(back, folderBack);
                graphics.DrawPolygon(edge, folderBack);
                Point[] folderFront = { new Point(2, 7), new Point(15, 7), new Point(13, 13), new Point(1, 13) };
                graphics.FillPolygon(front, folderFront);
                graphics.DrawPolygon(edge, folderFront);
            }
            return image;
        }
    }

    // Rounded card surface with 1px border, like the mockup panels.
    public class Card : Panel
    {
        private const int Radius = 12;

        public Card()
        {
            BackColor = Theme.Card;
            DoubleBuffered = true;
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            UpdateRegion();
            Invalidate();
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                // Cannot create a valid region for zero/negative size; skip until valid dimensions
                return;
            }
            Region old = Region;
            using (GraphicsPath p = UiGraphics.RoundedRect(new Rectangle(0, 0, Width, Height), Radius))
                Region = new Region(p);
            if (old != null) old.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= 1 || Height <= 1) return;
            using (GraphicsPath p = UiGraphics.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
            using (Pen pen = new Pen(Theme.Border, 1f))
                e.Graphics.DrawPath(pen, p);
        }
    }

    // Compact dark results viewport with an owner-drawn scrollbar. Focusing it
    // on pointer entry routes the mouse wheel here while over any result row.
    public class SearchResultsViewport : Panel
    {
        private int _contentHeight;
        private int _scrollOffset;

        public SearchResultsViewport()
        {
            BackColor = Theme.Card;
            TabStop = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void AddResultRow(Control row, int contentTop)
        {
            row.Tag = contentTop;
            Controls.Add(row);
            row.Top = contentTop - _scrollOffset;
        }

        public void ResetResults()
        {
            _contentHeight = 0;
            _scrollOffset = 0;
            LayoutRows();
            Invalidate();
        }

        public void SetContentHeight(int height)
        {
            _contentHeight = Math.Max(0, height);
            _scrollOffset = 0;
            LayoutRows();
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Focus();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int maxOffset = Math.Max(0, _contentHeight - ClientSize.Height);
            if (maxOffset == 0 || e.Delta == 0)
            {
                base.OnMouseWheel(e);
                return;
            }

            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            const int rowStep = 35;
            int target = _scrollOffset - (e.Delta / 120) * lines * rowStep;
            _scrollOffset = Math.Max(0, Math.Min(maxOffset, target));
            LayoutRows();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int maxOffset = Math.Max(0, _contentHeight - ClientSize.Height);
            if (maxOffset <= 0 || ClientSize.Height <= 0) return;

            int trackX = ClientSize.Width - 10;
            Rectangle track = new Rectangle(trackX + 2, 3, 4, ClientSize.Height - 6);
            using (GraphicsPath trackPath = UiGraphics.RoundedRect(track, 2))
            using (SolidBrush trackBrush = new SolidBrush(Theme.Input))
                e.Graphics.FillPath(trackBrush, trackPath);

            int thumbHeight = Math.Max(18, (int)((long)track.Height * ClientSize.Height / _contentHeight));
            thumbHeight = Math.Min(track.Height, thumbHeight);
            int thumbRange = Math.Max(0, track.Height - thumbHeight);
            int thumbY = track.Top + (int)((long)thumbRange * _scrollOffset / maxOffset);
            Rectangle thumb = new Rectangle(track.Left, thumbY, track.Width, thumbHeight);
            using (GraphicsPath thumbPath = UiGraphics.RoundedRect(thumb, 2))
            using (SolidBrush thumbBrush = new SolidBrush(Theme.Dim))
                e.Graphics.FillPath(thumbBrush, thumbPath);
        }

        private void LayoutRows()
        {
            foreach (Control control in Controls)
            {
                InputBox row = control as InputBox;
                if (row == null) continue;
                int contentTop = row.Tag is int ? (int)row.Tag : row.Top;
                row.Top = contentTop - _scrollOffset;
            }
        }
    }

    public sealed class AudioTransportButton : Control
    {
        private sealed class TransportAccessibleObject : Control.ControlAccessibleObject
        {
            private readonly AudioTransportButton _owner;

            public TransportAccessibleObject(AudioTransportButton owner) : base(owner)
            {
                _owner = owner;
            }

            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override string Name
            {
                get { return _owner.AccessibleName; }
                set { _owner.AccessibleName = value; }
            }
            public override string Description { get { return _owner.AccessibleDescription; } }
            public override string DefaultAction { get { return "Press"; } }
            public override void DoDefaultAction() { _owner.OnClick(EventArgs.Empty); }
        }

        public enum ActionKind { Play, Stop }

        private readonly ActionKind _action;
        private bool _hover;
        private bool _active;

        public string AudioPath { get; set; }

        public bool IsActive
        {
            get { return _active; }
            set
            {
                _active = value;
                AccessibleDescription = value ? "Audio preview is playing" : "Audio preview is not playing";
                Invalidate();
            }
        }

        public AudioTransportButton(ActionKind action)
        {
            _action = action;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(28, 28);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = action == ActionKind.Play ? "Play audio preview" : "Stop audio preview";
            Text = AccessibleName;
            AccessibleDescription = "Audio preview is not playing";
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TransportAccessibleObject(this);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Enter || key == Keys.Space || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                OnClick(EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 7 || Height < 7) return; // Minimum size for 6-radius rounded rect
            Rectangle bounds = new Rectangle(1, 1, Width - 3, Height - 3);
            if (_hover || Focused)
            {
                Color backColor = _active ? Theme.GreenSoft : (_hover ? Theme.ButtonHover : Theme.Button);
                using (GraphicsPath backPath = UiGraphics.RoundedRect(bounds, 6))
                using (SolidBrush backBrush = new SolidBrush(backColor))
                    e.Graphics.FillPath(backBrush, backPath);
            }
            else if (_active)
            {
                using (GraphicsPath backPath = UiGraphics.RoundedRect(bounds, 6))
                using (SolidBrush backBrush = new SolidBrush(Theme.GreenSoft))
                    e.Graphics.FillPath(backBrush, backPath);
            }

            Color iconColor = _action == ActionKind.Play ? Theme.GreenText : Theme.Light;
            if (_action == ActionKind.Play)
            {
                float centerX = Width / 2f;
                float centerY = Height / 2f;
                PointF[] triangle =
                {
                    new PointF(centerX - 5f, centerY - 8f),
                    new PointF(centerX + 7f, centerY),
                    new PointF(centerX - 5f, centerY + 8f)
                };
                using (SolidBrush brush = new SolidBrush(iconColor))
                    e.Graphics.FillPolygon(brush, triangle);
            }
            else
            {
                float centerX = Width / 2f;
                float top = (Height - 16f) / 2f;
                using (SolidBrush brush = new SolidBrush(iconColor))
                {
                    e.Graphics.FillRectangle(brush, centerX - 6f, top, 4f, 16f);
                    e.Graphics.FillRectangle(brush, centerX + 2f, top, 4f, 16f);
                }
            }

            if (Focused)
            {
                Rectangle focus = Rectangle.Inflate(bounds, -3, -3);
                using (Pen pen = new Pen(Theme.Border))
                    e.Graphics.DrawRectangle(pen, focus);
            }
        }
    }

    // Green rounded icon chip used as the section logo on each card header.
    public class IconChip : Control
    {
        private readonly string _glyph;

        public IconChip(string glyph)
        {
            _glyph = glyph;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(38, 38);
            BackColor = Theme.Card;
            Cursor = Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width <= 1 || Height <= 1) return;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = UiGraphics.RoundedRect(r, 10))
            using (SolidBrush b = new SolidBrush(Theme.GreenSoft))
                e.Graphics.FillPath(b, p);
            TextRenderer.DrawText(e.Graphics, _glyph, Theme.Glyph,
                new Rectangle(0, -1, Width, Height), Theme.GreenText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // Rounded flat button with optional MDL2 glyph before the text.
    public class RoundedButton : Control
    {
        public const int StyleNeutral = 0;
        public const int StyleGreen = 1;
        public const int StyleGhost = 2; // transparent, icon-only (inside inputs)

        private int _style;
        private bool _hover;
        private string _glyph = "";
        private Font _glyphFont = Theme.GlyphSmall;
        private Image _glyphImage;
        private Color _foreOverride = Color.Empty;

        public RoundedButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.Light;
            Size = new Size(110, 36);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        public int ButtonStyle
        {
            get { return _style; }
            set { _style = value; Invalidate(); }
        }

        public string Glyph
        {
            get { return _glyph; }
            set { _glyph = value ?? ""; Invalidate(); }
        }

        public Font GlyphFont
        {
            get { return _glyphFont; }
            set { _glyphFont = value ?? Theme.GlyphSmall; Invalidate(); }
        }

        public Image GlyphImage
        {
            get { return _glyphImage; }
            set { _glyphImage = value; Invalidate(); }
        }

        public Color ForeOverride
        {
            get { return _foreOverride; }
            set { _foreOverride = value; Invalidate(); }
        }

        private Color Back
        {
            get
            {
                if (_style == StyleGreen) return _hover ? Theme.GreenHover : Theme.Green;
                if (_style == StyleGhost) return _hover ? Theme.ButtonHover : Color.Transparent;
                return _hover ? Theme.ButtonHover : Theme.Button;
            }
        }

        private Color Fore
        {
            get
            {
                if (_foreOverride != Color.Empty) return _foreOverride;
                if (_style == StyleGreen) return Color.FromArgb(16, 26, 20);
                return Theme.Light;
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width <= 1 || Height <= 1) return;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color back = Enabled ? Back : (_style == StyleGhost ? Color.Transparent : Theme.Input);
            using (GraphicsPath p = UiGraphics.RoundedRect(r, 8))
            {
                if (back != Color.Transparent)
                using (SolidBrush b = new SolidBrush(back))
                    e.Graphics.FillPath(b, p);
                if (Enabled && _style != StyleGhost)
                using (Pen pen = new Pen(_style == StyleGreen ? Theme.Green : Theme.Border, 1f))
                    e.Graphics.DrawPath(pen, p);
            }
            Color fore = Enabled ? Fore : Theme.Faint;
            int glyphW = 0;
            if (_glyphImage != null)
                glyphW = _glyphImage.Width;
            else if (_glyph.Length > 0)
                glyphW = TextRenderer.MeasureText(e.Graphics, _glyph, _glyphFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            string text = Text ?? "";
            Size ts = text.Length > 0
                ? TextRenderer.MeasureText(e.Graphics, text, Font, Size.Empty, TextFormatFlags.NoPadding)
                : Size.Empty;
            int total = glyphW + (glyphW > 0 && text.Length > 0 ? 8 : 0) + ts.Width;
            int x = (Width - total) / 2;
            Rectangle full = new Rectangle(0, 0, Width, Height);
            if (_glyphImage != null)
            {
                int iconHeight = Math.Min(_glyphImage.Height, Height - 8);
                int iconWidth = Math.Min(_glyphImage.Width, Width);
                e.Graphics.DrawImage(_glyphImage,
                    new Rectangle(x, (Height - iconHeight) / 2, iconWidth, iconHeight));
                x += glyphW + 8;
            }
            else if (_glyph.Length > 0)
            {
                TextRenderer.DrawText(e.Graphics, _glyph, _glyphFont,
                    new Rectangle(x, 0, glyphW + 2, Height), fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += glyphW + 8;
            }
            if (text.Length > 0)
            {
                TextRenderer.DrawText(e.Graphics, text, Font,
                    new Rectangle(x, 0, Math.Max(1, Width - x), Height), fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // Dark rounded input wrapping a borderless TextBox; optional left icon and
    // right inside-button (e.g. clipboard paste, clear query).
    public class InputBox : Panel
    {
        public readonly TextBox Inner;
        private string _leftGlyph = "";
        private Color _leftGlyphColor = Theme.Dim;
        private Image _leftImage;

        public InputBox()
        {
            BackColor = Theme.Input;
            DoubleBuffered = true;
            Inner = new TextBox();
            Inner.BorderStyle = BorderStyle.None;
            Inner.BackColor = Theme.Input;
            Inner.ForeColor = Theme.Light;
            Inner.Font = Theme.Regular;
            Controls.Add(Inner);
            Height = 38;
            Resize += delegate { LayoutInner(); };
            LayoutInner();
        }

        public string LeftGlyph
        {
            get { return _leftGlyph; }
            set
            {
                _leftGlyph = value ?? "";
                Inner.Padding = new Padding(HasLeftIcon ? 32 : 8, 0, 0, 0);
                Invalidate();
                LayoutInner();
            }
        }

        public Image LeftImage
        {
            get { return _leftImage; }
            set
            {
                _leftImage = value;
                Inner.Padding = new Padding(HasLeftIcon ? 32 : 8, 0, 0, 0);
                Invalidate();
                LayoutInner();
            }
        }

        private bool HasLeftIcon { get { return _leftGlyph.Length > 0 || _leftImage != null; } }

        public Color LeftGlyphColor
        {
            get { return _leftGlyphColor; }
            set { _leftGlyphColor = value; Invalidate(); }
        }

        public RoundedButton AddRightButton(string glyph, string accessibleName, EventHandler onClick)
        {
            RoundedButton b = new RoundedButton();
            b.ButtonStyle = RoundedButton.StyleGhost;
            b.Glyph = glyph;
            b.AccessibleName = accessibleName;
            b.Size = new Size(32, Height - 8);
            b.Click += onClick;
            Controls.Add(b);
            b.BringToFront();
            b.Location = new Point(Width - b.Width - 4, 4);
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Inner.Padding = new Padding(HasLeftIcon ? 32 : 8, 0, b.Width + 10, 0);
            b.Resize += delegate { Inner.Padding = new Padding(HasLeftIcon ? 32 : 8, 0, b.Width + 10, 0); };
            return b;
        }

        private void LayoutInner()
        {
            int left = HasLeftIcon ? 32 : 10;
            int right = 10;
            foreach (Control c in Controls)
            {
                RoundedButton rb = c as RoundedButton;
                if (rb != null && rb.ButtonStyle == RoundedButton.StyleGhost)
                    right = rb.Width + 12;
            }
            Inner.SetBounds(left, (Height - Inner.Height) / 2, Math.Max(10, Width - left - right), Inner.Height);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (Width <= 0 || Height <= 0)
            {
                Invalidate();
                return;
            }
            Region old = Region;
            using (GraphicsPath p = UiGraphics.RoundedRect(new Rectangle(0, 0, Width, Height), 8))
                Region = new Region(p);
            if (old != null) old.Dispose();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= 1 || Height <= 1) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = UiGraphics.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            using (Pen pen = new Pen(Theme.Border, 1f))
                e.Graphics.DrawPath(pen, p);
            if (_leftImage != null)
            {
                int iconWidth = Math.Min(16, _leftImage.Width);
                int iconHeight = Math.Min(16, _leftImage.Height);
                e.Graphics.DrawImage(_leftImage,
                    new Rectangle(11, (Height - iconHeight) / 2, iconWidth, iconHeight));
            }
            else if (_leftGlyph.Length > 0)
            {
                TextRenderer.DrawText(e.Graphics, _leftGlyph, Theme.GlyphSmall,
                    new Rectangle(8, 0, 22, Height), _leftGlyphColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // Slim rounded progress bar (mockup: thin track with green fill).
    public class ProgressLite : Control
    {
        private double _value; // 0..100

        public ProgressLite()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 8;
        }

        public double Value
        {
            get { return _value; }
            set
            {
                double v = Math.Max(0, Math.Min(100, value));
                if (Math.Abs(v - _value) < 0.01) return;
                _value = v;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width <= 1 || Height <= 1) return;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = UiGraphics.RoundedRect(r, 4))
            using (SolidBrush track = new SolidBrush(Theme.Input))
                e.Graphics.FillPath(track, p);
            if (_value > 0)
            {
                int w = (int)Math.Round((Width - 1) * _value / 100.0);
                if (w > 0)
                using (GraphicsPath p = UiGraphics.RoundedRect(new Rectangle(0, 0, Math.Max(w, Height), Height - 1), 4))
                using (SolidBrush fill = new SolidBrush(Theme.Green))
                    e.Graphics.FillPath(fill, p);
            }
        }
    }

    // Dark owner-drawn dropdown replacing the system-light ComboBox (user
    // feedback 2026-10-26). The closed control paints like an InputBox with
    // a green chevron; the popup is a small borderless form in the app
    // palette (dark rows, green hover), so no system-white list can appear.
    public class DarkCombo : Control
    {
        private string[] _items = new string[0];
        private int _index = -1;
        private ComboPopup _popup;
        private DateTime _closedAt = DateTime.MinValue;

        public event EventHandler SelectedIndexChanged;

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; _index = _items.Length > 0 ? 0 : -1; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                int v = value;
                if (v >= _items.Length) v = -1;
                if (v == _index) return;
                _index = v;
                Invalidate();
                EventHandler h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public string SelectedText
        {
            get { return (_index >= 0 && _index < _items.Length) ? _items[_index] : ""; }
        }

        public DarkCombo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Input;
            ForeColor = Theme.Light;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width <= 1 || Height <= 1) return;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = UiGraphics.RoundedRect(r, 8))
            {
                using (SolidBrush b = new SolidBrush(Theme.Input)) e.Graphics.FillPath(b, p);
                using (Pen pen = new Pen(Theme.Border, 1f)) e.Graphics.DrawPath(pen, p);
            }
            const int chevronW = 34;
            Rectangle textRect = new Rectangle(10, 0, Math.Max(1, Width - chevronW - 12), Height);
            TextRenderer.DrawText(e.Graphics, SelectedText, Font, textRect, Theme.Light,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, "\uE70D", Theme.GlyphSmall,
                new Rectangle(Width - chevronW, 0, chevronW - 10, Height), Theme.GreenText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            // A click that closed the popup must not instantly reopen it.
            if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 350) return;
            OpenPopup();
        }

        private void OpenPopup()
        {
            if (_items.Length == 0) return;
            if (_popup != null && !_popup.IsDisposed) { _popup.Close(); return; }
            ComboPopup popup = new ComboPopup(_items, _index, Font, Width);
            popup.ItemChosen += delegate(object s, ComboPopup.ItemArgs a)
            {
                popup.Close();
                SelectedIndex = a.Index; // raises SelectedIndexChanged on change only
            };
            popup.FormClosed += delegate
            {
                _closedAt = DateTime.UtcNow;
                if (_popup == popup) _popup = null;
            };
            Point screen = PointToScreen(new Point(0, Height));
            popup.StartPosition = FormStartPosition.Manual;
            popup.Bounds = new Rectangle(screen, popup.PopupSize);
            popup.Show(this);
            _popup = popup;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _popup != null && !_popup.IsDisposed) _popup.Close();
            base.Dispose(disposing);
        }
    }

    // Borderless dropdown surface for DarkCombo: rows are plain dark buttons,
    // clicking one closes the popup and reports the index.
    internal sealed class ComboPopup : Form
    {
        public sealed class ItemArgs : EventArgs
        {
            public readonly int Index;
            public ItemArgs(int index) { Index = index; }
        }

        public event EventHandler<ItemArgs> ItemChosen;

        private const int RowH = 30;
        private const int Pad = 6;

        internal sealed class Row : Control
        {
            private readonly string _text;
            private readonly int _rowIndex;
            private readonly bool _selected;
            private readonly ComboPopup _owner;
            private bool _hover;

            public Row(ComboPopup owner, string text, int rowIndex, bool selected)
            {
                _owner = owner;
                _text = text;
                _rowIndex = rowIndex;
                _selected = selected;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Input;
                ForeColor = Theme.Light;
                Cursor = Cursors.Hand;
                TabStop = false;
                MouseEnter += delegate { _hover = true; Invalidate(); };
                MouseLeave += delegate { _hover = false; Invalidate(); };
                Click += delegate { _owner.Choose(_rowIndex); };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Color bg = _hover ? Theme.ButtonHover : (_selected ? Theme.GreenSoft : Theme.Input);
                using (SolidBrush b = new SolidBrush(bg))
                    e.Graphics.FillRectangle(b, ClientRectangle);
                Color fore = _selected ? Theme.GreenText : Theme.Light;
                TextRenderer.DrawText(e.Graphics, _text, Font,
                    new Rectangle(10, 0, Math.Max(1, Width - 16), Height), fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        public ComboPopup(string[] items, int selectedIndex, Font font, int minWidth)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Input;
            DoubleBuffered = true;
            KeyPreview = true;

            Font = font;
            int w = minWidth;
            for (int i = 0; i < items.Length; i++)
            {
                Row row = new Row(this, items[i], i, i == selectedIndex);
                row.Font = font;
                row.SetBounds(Pad, Pad + i * RowH, 0, RowH);
                Controls.Add(row);
                w = Math.Max(w, TextRenderer.MeasureText(items[i], font).Width + 60);
            }
            foreach (Control c in Controls) c.Width = w - Pad * 2;
            ClientSize = new Size(w, Pad * 2 + items.Length * RowH);

            Deactivate += delegate { Close(); };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
            };
        }

        internal void Choose(int index)
        {
            EventHandler<ItemArgs> h = ItemChosen;
            if (h != null) h(this, new ItemArgs(index));
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (Width <= 0 || Height <= 0)
            {
                return;
            }
            Region old = Region;
            using (GraphicsPath p = UiGraphics.RoundedRect(new Rectangle(0, 0, Width, Height), 8))
                Region = new Region(p);
            if (old != null) old.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= 1 || Height <= 1) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = UiGraphics.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            using (Pen pen = new Pen(Theme.Border, 1f))
                e.Graphics.DrawPath(pen, p);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ChromeApi.WmNcCalcSize && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        public Size PopupSize
        {
            get { return new Size(ClientSize.Width + 1, ClientSize.Height + 1); }
        }
    }

    // Static helpers to make stock controls dark (ListView + its header).
    internal static class DarkControls
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        private const int LvmGetHeader = 0x101F;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void MakeDark(ListView lv)
        {
            lv.HandleCreated += delegate { Apply(lv); };
            if (lv.IsHandleCreated) Apply(lv);
        }

        // The header control is created after the ListView handle; re-apply a bit
        // later (called from OnShown and from a short retry timer).
        public static void Reapply(ListView lv)
        {
            if (lv != null && lv.IsHandleCreated) Apply(lv);
        }

        private static void Apply(ListView lv)
        {
            try
            {
                SetWindowTheme(lv.Handle, "DarkMode_Explorer", null);
                IntPtr header = SendMessage(lv.Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_Explorer", null);
            }
            catch { }
        }
    }
}
