using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DownloadSearchAudio
{
    // Cookie management dialog, simplified adaptation of YouTubeDownloader-Net10
    // src\CookieManagerForm.cs. It owns no download or cookie logic: it reports
    // the state of the app's saved cookie jar and offers the two user-visible
    // actions around it - get/refresh the jar from a browser (the caller runs
    // the browser-chooser/export flow) and delete the jar (the only destructive
    // operation, and one that touches exactly one file and its .tmp sibling).
    //
    // The dialog never reads cookie values and never shows the file contents;
    // the date comes from the file's own timestamp and the source browser comes
    // from non-secret metadata that is written only after a successful export.
    public sealed class CookiesDialog : Form
    {
        // Close = the user wants nothing else. GetFromBrowser = the caller
        // should run the browser-chooser/export flow.
        public const int ActionClose = 0;
        public const int ActionGetFromBrowser = 1;

        private const int Pad = 16;
        private const int DialogW = 560;

        private readonly Settings _settings;
        private Label _lblState;
        private Label _lblDetails;
        private Label _lblHint;
        private Label _lblStatus;
        private RoundedButton _btnGet;
        private RoundedButton _btnDelete;
        private RoundedButton _btnClose;
        private int _action = ActionClose;

        private CookiesDialog(Settings settings)
        {
            _settings = settings;

            Text = "YouTube cookies";
            // Same custom dark chrome as the main window and the other dialogs.
            FormBorderStyle = FormBorderStyle.None;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Card;
            DoubleBuffered = true;

            DarkTitleBar titleBar = new DarkTitleBar(this);
            Controls.Add(titleBar);
            titleBar.BringToFront();
            int titleH = DarkTitleBar.BarHeight;

            _lblState = new Label();
            _lblState.Location = new Point(Pad, 14 + titleH);
            _lblState.Size = new Size(DialogW - Pad * 2, 22);
            _lblState.Font = Theme.RegularBold;
            Controls.Add(_lblState);

            _lblDetails = new Label();
            _lblDetails.Location = new Point(Pad, 42 + titleH);
            _lblDetails.Size = new Size(DialogW - Pad * 2, 40);
            _lblDetails.ForeColor = Theme.Light;
            Controls.Add(_lblDetails);

            _lblHint = new Label();
            _lblHint.Location = new Point(Pad, 86 + titleH);
            _lblHint.Size = new Size(DialogW - Pad * 2, 62);
            _lblHint.ForeColor = Theme.Dim;
            _lblHint.Text = "Cookies are needed when YouTube answers with a sign-in / bot check or HTTP 429. "
                + "The saved cookies stay in this application's data folder, are never shown or logged, "
                + "and are not part of the distributed build.";
            Controls.Add(_lblHint);

            const int btnH = 32;
            const int gap = 10;
            const int closeW = 100;
            const int deleteW = 190;
            int rowY = 158 + titleH;
            int closeX = DialogW - Pad - closeW;
            int deleteX = closeX - gap - deleteW;
            int getW = deleteX - gap - Pad;

            _btnGet = new RoundedButton();
            _btnGet.Location = new Point(Pad, rowY);
            _btnGet.Size = new Size(getW, btnH);
            _btnGet.ButtonStyle = RoundedButton.StyleGreen;
            _btnGet.Font = Theme.RegularBold;
            _btnGet.Text = "Save from browser...";
            _btnGet.Click += delegate { _action = ActionGetFromBrowser; Close(); };
            Controls.Add(_btnGet);

            _btnDelete = new RoundedButton();
            _btnDelete.Location = new Point(deleteX, rowY);
            _btnDelete.Size = new Size(deleteW, btnH);
            _btnDelete.ButtonStyle = RoundedButton.StyleNeutral;
            _btnDelete.Font = Theme.Regular;
            _btnDelete.Text = "Delete saved cookies";
            _btnDelete.Click += delegate { OnDeleteClicked(); };
            Controls.Add(_btnDelete);

            _btnClose = new RoundedButton();
            _btnClose.Location = new Point(closeX, rowY);
            _btnClose.Size = new Size(closeW, btnH);
            _btnClose.ButtonStyle = RoundedButton.StyleNeutral;
            _btnClose.Font = Theme.Regular;
            _btnClose.Text = "Close";
            _btnClose.Click += delegate { _action = ActionClose; Close(); };
            Controls.Add(_btnClose);

            _lblStatus = new Label();
            _lblStatus.Location = new Point(Pad, 200 + titleH);
            _lblStatus.Size = new Size(DialogW - Pad * 2, 36);
            _lblStatus.ForeColor = Theme.Dim;
            Controls.Add(_lblStatus);

            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { _action = ActionClose; Close(); }
            };

            RefreshState();

            ClientSize = new Size(DialogW, 200 + titleH + 36 + 12);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Same chrome as the main window; the non-client frame is removed,
            // so the custom dark strip must paint above the removed frame area.
            ChromeApi.ApplyChromeStyles(Handle);
            ChromeApi.ApplyRoundedCorners(Handle);
        }

        // Reports presence, the file's own timestamp and (only when it is truly
        // known) the browser the jar was exported from. Unknown stays unknown:
        // nothing is guessed and no value is invented.
        private void RefreshState()
        {
            bool present = CookieStore.HasUsableCookies();
            _lblState.Text = present ? "Saved YouTube cookies are present." : "No saved YouTube cookies.";
            _lblState.ForeColor = present ? Theme.GreenText : Theme.Dim;

            System.Text.StringBuilder details = new System.Text.StringBuilder();
            if (present)
            {
                try
                {
                    DateTime changed = File.GetLastWriteTime(CookieStore.FilePath);
                    if (changed.Year > 1601)
                        details.AppendLine("Saved on: " + changed.ToString("yyyy-MM-dd HH:mm"));
                }
                catch { }
                string key = _settings != null ? _settings.CookiesSourceBrowser : null;
                details.AppendLine("Saved from: " + (BrowserCookies.IsSupported(key)
                    ? BrowserCookies.DisplayForKey(key) : "unknown source"));
            }
            _lblDetails.Text = details.ToString();

            // A jar that does not exist cannot be deleted.
            _btnDelete.Enabled = present;
        }

        private void OnDeleteClicked()
        {
            DialogResult ans = MessageBox.Show(this,
                "Delete the saved YouTube cookies? This removes only the saved cookie file, "
                + "never browser data or any other file.",
                "Download and Search Audio", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ans != DialogResult.Yes) return;

            CookieClearResult cleared = CookieStore.ClearSaved();
            if (cleared.Status == CookieClearStatus.Removed)
            {
                // The recorded source is only meaningful while the jar it
                // describes exists; drop it together with the file.
                if (_settings != null)
                {
                    _settings.CookiesSourceBrowser = null;
                    _settings.Save();
                }
                _lblStatus.ForeColor = Theme.GreenText;
                _lblStatus.Text = "Saved cookies deleted.";
            }
            else if (cleared.Status == CookieClearStatus.NothingToRemove)
            {
                _lblStatus.ForeColor = Theme.Dim;
                _lblStatus.Text = "There was nothing saved.";
            }
            else
            {
                _lblStatus.ForeColor = Theme.Red;
                _lblStatus.Text = "Could not delete the saved cookies: " + (cleared.Error ?? "");
            }
            RefreshState();
        }

        // Borderless window: dragging the top chrome strip moves the dialog.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ChromeApi.WmNcCalcSize && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        public static int Show(Form owner, Settings settings)
        {
            using (CookiesDialog f = new CookiesDialog(settings))
            {
                if (owner == null || owner.IsDisposed)
                {
                    if (Application.OpenForms.Count > 0) owner = Application.OpenForms[0];
                }
                if (owner != null && !owner.IsDisposed) f.ShowDialog(owner);
                else f.ShowDialog();
                return f._action;
            }
        }
    }

    // Browser picker for the cookie export: lists only installed supported
    // browsers (data-directory presence check, no profile reading), with the
    // system default browser preselected. Returns the yt-dlp browser key, or
    // null when the user cancelled.
    public sealed class BrowserPickDialog : Form
    {
        private ComboBox _cbo;
        private List<BrowserInfo> _browsers;
        private bool _chosen;

        private BrowserPickDialog(List<BrowserInfo> browsers, string defaultKey)
        {
            _browsers = browsers;

            Text = "Save YouTube cookies from browser";
            FormBorderStyle = FormBorderStyle.None;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Card;
            DoubleBuffered = true;

            DarkTitleBar titleBar = new DarkTitleBar(this);
            Controls.Add(titleBar);
            titleBar.BringToFront();
            int titleH = DarkTitleBar.BarHeight;

            const int w = 420;
            Label msg = new Label();
            msg.Location = new Point(16, 14 + titleH);
            msg.Size = new Size(w - 32, 34);
            msg.ForeColor = Theme.Light;
            msg.Font = Theme.Regular;
            msg.Text = "Choose a browser that is signed in to youtube.com:";
            msg.MaximumSize = new Size(w - 32, 0);
            Controls.Add(msg);

            _cbo = new ComboBox();
            _cbo.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbo.FlatStyle = FlatStyle.Flat;
            _cbo.BackColor = Theme.Input;
            _cbo.ForeColor = Theme.Light;
            _cbo.Font = Theme.Regular;
            int selectedIndex = 0;
            for (int i = 0; i < browsers.Count; i++)
            {
                BrowserInfo b = browsers[i];
                _cbo.Items.Add(b.IsDefault ? b.Display + " (default)" : b.Display);
                if (!string.IsNullOrEmpty(defaultKey) && string.Equals(b.Key, defaultKey, StringComparison.OrdinalIgnoreCase))
                    selectedIndex = i;
            }
            _cbo.SelectedIndex = selectedIndex;
            _cbo.Location = new Point(16, 54 + titleH);
            _cbo.Size = new Size(w - 32, 30);
            Controls.Add(_cbo);

            const int btnH = 32;
            int rowY = 100 + titleH;
            RoundedButton btnOk = new RoundedButton();
            btnOk.Text = "Save cookies";
            btnOk.ButtonStyle = RoundedButton.StyleGreen;
            btnOk.Font = Theme.RegularBold;
            btnOk.Location = new Point(w - 16 - 130, rowY);
            btnOk.Size = new Size(130, btnH);
            btnOk.Click += delegate { _chosen = true; Close(); };
            Controls.Add(btnOk);

            RoundedButton btnCancel = new RoundedButton();
            btnCancel.Text = "Cancel";
            btnCancel.ButtonStyle = RoundedButton.StyleNeutral;
            btnCancel.Font = Theme.Regular;
            btnCancel.Location = new Point(w - 16 - 130 - 10 - 110, rowY);
            btnCancel.Size = new Size(110, btnH);
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);

            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
            };

            ClientSize = new Size(w, rowY + btnH + 14);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
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

        public static string Show(Form owner, List<BrowserInfo> browsers, string defaultKey)
        {
            using (BrowserPickDialog f = new BrowserPickDialog(browsers, defaultKey))
            {
                if (owner == null || owner.IsDisposed)
                {
                    if (Application.OpenForms.Count > 0) owner = Application.OpenForms[0];
                }
                if (owner != null && !owner.IsDisposed) f.ShowDialog(owner);
                else f.ShowDialog();
                if (!f._chosen) return null;
                return f._browsers[f._cbo.SelectedIndex].Key;
            }
        }
    }
}
