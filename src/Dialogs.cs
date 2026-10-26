using System;
using System.Drawing;
using System.Windows.Forms;

namespace DownloadSearchAudio
{
    // Dark themed message dialog (MessageBox replacement with the app chrome).
    // Used for duplicate hits (with paths), pre-check failures and download
    // errors so messages stay readable in the dark UI.
    public class DarkDialog : Form
    {
        public DarkDialog(string title, string message, string[] detailLines, string okText)
            : this(title, message, detailLines, okText, false)
        {
        }

        private DarkDialog(string title, string message, string[] detailLines, string okText, bool plainTextDetails)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Card;
            DoubleBuffered = true;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            DarkTitleBar bar = new DarkTitleBar(this);
            Controls.Add(bar);

            int w = 560;
            int contentTop = DarkTitleBar.BarHeight + 16;

            Label msg = new Label();
            msg.Text = message;
            msg.Font = Theme.Regular;
            msg.ForeColor = Theme.Light;
            msg.SetBounds(18, contentTop, w - 36, 20);
            msg.BackColor = Theme.Card;
            Controls.Add(msg);
            int msgH = Math.Max(20, TextRenderer.MeasureText(message, Theme.Regular,
                new Size(w - 36, 1000), TextFormatFlags.WordBreak).Height + 4);
            msg.Height = msgH;

            int y = contentTop + msgH + 8;
            if (detailLines != null && detailLines.Length > 0)
            {
                if (plainTextDetails)
                {
                    string detailText = string.Join(Environment.NewLine, detailLines);
                    Size available = new Size(w - 52, 150);
                    Size measured = TextRenderer.MeasureText(detailText, Theme.Small, available,
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                    int dh = Math.Min(150, Math.Max(36, measured.Height + 16));

                    Label details = new Label();
                    details.Text = detailText;
                    details.Font = Theme.Small;
                    details.ForeColor = Theme.Light;
                    details.BackColor = Theme.Input;
                    details.Padding = new Padding(8, 6, 8, 6);
                    details.AutoSize = false;
                    details.AutoEllipsis = true;
                    details.UseMnemonic = false;
                    details.SetBounds(18, y, w - 36, dh);
                    Controls.Add(details);
                    y += dh + 8;
                }
                else
                {
                    TextBox details = new TextBox();
                    details.Multiline = true;
                    details.ReadOnly = true;
                    details.ScrollBars = ScrollBars.Vertical;
                    details.BackColor = Theme.Input;
                    details.ForeColor = Theme.Light;
                    details.BorderStyle = BorderStyle.FixedSingle;
                    details.Font = new Font("Consolas", 9F);
                    details.Text = string.Join(Environment.NewLine, detailLines);
                    int dh = Math.Min(180, 6 + detailLines.Length * 18);
                    details.SetBounds(18, y, w - 36, dh);
                    Controls.Add(details);
                    y += dh + 8;
                }
            }

            RoundedButton ok = new RoundedButton();
            ok.Text = string.IsNullOrEmpty(okText) ? "OK" : okText;
            ok.ButtonStyle = RoundedButton.StyleGreen;
            ok.Font = Theme.RegularBold;
            ok.SetBounds(w - 18 - 110, y + 6, 110, 34);
            ok.Click += delegate { Close(); };
            Controls.Add(ok);

            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
            };

            Height = y + 6 + 34 + 16;
            Width = w;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Same chrome as the main window; without this the dialog shows a
            // system-drawn light strip where the non-client frame should have
            // been removed.
            ChromeApi.ApplyChromeStyles(Handle);
            ChromeApi.ApplyRoundedCorners(Handle);
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

        public static void ShowInfo(IWin32Window owner, string title, string message, string[] detailLines)
        {
            using (DarkDialog dlg = new DarkDialog(title, message, detailLines, "OK"))
                dlg.ShowDialog(owner);
        }

        public static void ShowDuplicateWarning(IWin32Window owner, string[] existingPaths)
        {
            using (DarkDialog dlg = new DarkDialog(
                "File already exists",
                "A file with this name already exists in the save folder, so the download was stopped.\n\nExisting file(s):",
                existingPaths, "OK", true))
                dlg.ShowDialog(owner);
        }
    }
}
