using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DownloadSearchAudio
{
    // Main window: dark UI with Download Audio / Music Search
    // cards and a status bar. Stage 1 is the UI skeleton: real window chrome,
    // real dialogs/settings where wired, honest disabled/empty states for the
    // not-yet-implemented download/search/trim actions (Stage 2+).
    // Narrow compact layout: 720x864 window (user feedback 2026-10-26: the
    // window was too wide and the URL/path fields dominated it).
    public class MainForm : Form
    {
        private const int DefaultW = 720;
        private const int DefaultH = 676;
        private const int DownloadCardH = 288;
        private const int SearchCardH = 300;
        private const int DownloadY = 8;
        private const int SearchY = DownloadY + DownloadCardH + 12;
        private const int StatusBarH = 26;
        private const int PreviewButtonSize = 28;
        private const int PreviewButtonGap = 4;
        private const int PreviewRightPadding = 4;

        private readonly Settings _settings = new Settings(AppPaths.SettingsPath);

        private DarkTitleBar _titleBar;
        private ChromePanel _content;
        private Panel _statusBar;
        private Label _lblStatus;
        private Label _lblStatusRight;

        private Card _pnlDownload;
        private InputBox _inUrl;
        private DarkCombo _cboFormat;
        private InputBox _inSaveTo;
        private RoundedButton _btnDownload;
        private RoundedButton _btnCancelDownload;
        private RoundedButton _btnOpenSaveFolder;
        private ProgressLite _progress;
        private Label _lblPercent;
        private Label _lblDlStatus;

        // Download state (Stage 2): one download at a time.
        private volatile Process _ytProc;
        private volatile bool _downloading;
        private volatile bool _cancelRequested;
        private volatile bool _checking;
        private volatile bool _cookieBusy;   // a browser export run is active
        private string _downloadDest;   // [download] Destination
        private string _extractDest;    // [ExtractAudio] Destination
        private DateTime _downloadStartUtc;
        private StagedCookieFile _stagedCookie; // jar copy used by THIS attempt
        private RoundedButton _btnCookies;

        private Card _pnlSearch;
        private InputBox _inSearchFolder;
        private CheckBox _chkSubfolders;
        private InputBox _inQuery;
        private RoundedButton _btnSearch;
        private SearchResultsViewport _searchResult;
        private Label[] _searchResultHeaders;
        private Label _searchStatus;
        private readonly List<InputBox> _searchResultRows = new List<InputBox>();
        private readonly AudioPreviewPlayer _audioPreview = new AudioPreviewPlayer();

        public MainForm()
        {
            Text = "Download and Search Audio";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.Back;
            DoubleBuffered = true;
            MinimumSize = new Size(DefaultW, DefaultH);
            ClientSize = new Size(DefaultW, DefaultH);

            _settings.Load();

            _titleBar = new DarkTitleBar(this);
            Icon appIcon = LoadAppIcon();
            if (appIcon != null)
            {
                Icon = appIcon;              // taskbar + Alt-Tab
                _titleBar.AppIcon = appIcon; // custom title bar drawing
            }
            Controls.Add(_titleBar);

            _content = new ChromePanel(this);
            _content.BackColor = Theme.Back;
            _content.Dock = DockStyle.Fill;
            Controls.Add(_content);
            _content.BringToFront();

            BuildStatusBar();
            BuildDownloadCard();
            BuildSearchCard();
            _audioPreview.PlaybackFailed += OnPreviewPlaybackFailed;
            _audioPreview.PlaybackChanged += OnPreviewPlaybackChanged;

            PositionInitial();
        }

        // App icon comes from the assembly itself (embedded .ico), so the EXE
        // has no external file dependency. Falls back to the EXE icon resource.
        private static Icon LoadAppIcon()
        {
            try
            {
                using (Stream s = typeof(MainForm).Assembly.GetManifestResourceStream("DownloadSearchAudio.app.ico"))
                {
                    if (s != null) return new Icon(s);
                }
            }
            catch { }
            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }
            return null;
        }

        // Put the window near the bottom-right of the work area so it does not
        // cover the user's central workspace.
        private void PositionInitial()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int x = wa.Right - Width - Math.Max(16, (wa.Width - Width) / 6);
            int y = wa.Bottom - Height - 16;
            if (y < wa.Top) y = wa.Top;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Math.Max(wa.Left, x), y);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyChromeStyles();
            ChromeApi.ApplyRoundedCorners(Handle);
            ApplyPersistedSettings();
        }

        // ReadOnly path boxes display the START of the text; after setting a
        // path, move the caret to the end so the folder NAME stays visible
        // when the path is longer than the box.
        private static void ShowPathTail(InputBox box)
        {
            TextBox t = box.Inner;
            t.SelectionStart = t.TextLength;
            t.SelectionLength = 0;
            t.ScrollToCaret();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
        }

        private void ApplyChromeStyles()
        {
            ChromeApi.ApplyChromeStyles(Handle);
        }

        private void ApplyPersistedSettings()
        {
            if (!string.IsNullOrEmpty(_settings.SearchFolder) && Directory.Exists(_settings.SearchFolder))
            {
                _inSearchFolder.Inner.Text = _settings.SearchFolder;
                ShowPathTail(_inSearchFolder);
            }
            else if (!string.IsNullOrEmpty(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
            {
                _inSearchFolder.Inner.Text = _settings.LastFolder;
                ShowPathTail(_inSearchFolder);
            }
            if (!string.IsNullOrEmpty(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
            {
                _inSaveTo.Inner.Text = _settings.LastFolder;
                ShowPathTail(_inSaveTo);
            }
            _chkSubfolders.Checked = _settings.IncludeSubfolders;
            if (string.Equals(_settings.AudioFormat, "original", StringComparison.OrdinalIgnoreCase))
                _cboFormat.SelectedIndex = 1;
            _cboFormat.SelectedIndexChanged += delegate
            {
                _settings.AudioFormat = AudioOutputModeIo.ToSetting(
                    _cboFormat.SelectedIndex == 0 ? AudioOutputMode.Mp3 : AudioOutputMode.Original);
                _settings.Save();
            };
        }

        private void SaveSettings()
        {
            if (_inSaveTo.Inner.Text.Trim().Length > 0 && Directory.Exists(_inSaveTo.Inner.Text.Trim()))
            {
                _settings.LastFolder = _inSaveTo.Inner.Text.Trim();
                _settings.HasChosenFolder = true;
            }
            if (_inSearchFolder.Inner.Text.Trim().Length > 0 && Directory.Exists(_inSearchFolder.Inner.Text.Trim()))
                _settings.SearchFolder = _inSearchFolder.Inner.Text.Trim();
            _settings.IncludeSubfolders = _chkSubfolders.Checked;
            _settings.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // A running download is canceled with the window; the user decides
            // via the standard confirm dialog.
            if (_downloading && e.CloseReason == CloseReason.UserClosing)
            {
                DialogResult r = MessageBox.Show(this,
                    "A download is in progress. Cancel it and close?",
                    "Download and Search Audio", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cancelRequested = true;
                YtDlpRunner.KillTree(_ytProc);
            }
            SaveSettings();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _audioPreview.Dispose();
            SaveSettings();
            AppPaths.CleanupSessionWorkDir();
            base.OnFormClosed(e);
        }

        // ---------- window chrome (WM messages, adapted from YouTubeDownloader) ----------

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ChromeApi.WmDpiChanged)
            {
                HandleDpiChanged(ref m);
                return;
            }
            if (m.Msg == ChromeApi.WmGetMinMaxInfo)
            {
                base.WndProc(ref m);
                IntPtr mon = ChromeApi.MonitorFromWindow(m.HWnd, ChromeApi.MonitorDefaultToNearest);
                ChromeApi.MonitorInfo mi = new ChromeApi.MonitorInfo();
                mi.CbSize = (uint)Marshal.SizeOf(typeof(ChromeApi.MonitorInfo));
                if (mon != IntPtr.Zero && ChromeApi.GetMonitorInfo(mon, ref mi))
                {
                    Marshal.WriteInt32(m.LParam, 8, mi.Work.Right - mi.Work.Left);
                    Marshal.WriteInt32(m.LParam, 12, mi.Work.Bottom - mi.Work.Top);
                    Marshal.WriteInt32(m.LParam, 16, mi.Work.Left);
                    Marshal.WriteInt32(m.LParam, 20, mi.Work.Top);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == ChromeApi.WmNcCalcSize && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == ChromeApi.WmNcHitTest)
            {
                int sx = unchecked((short)((long)m.LParam & 0xFFFF));
                int sy = unchecked((short)(((long)m.LParam >> 16) & 0xFFFF));
                Point cpt = PointToClient(new Point(sx, sy));
                bool maximized = ChromeApi.IsZoomedStyle(m.HWnd);
                if (!maximized)
                {
                    int fw = ChromeApi.GetSystemMetrics(ChromeApi.SmCxSizeFrame) + ChromeApi.GetSystemMetrics(ChromeApi.SmCxPaddedBorder);
                    int fh = ChromeApi.GetSystemMetrics(ChromeApi.SmCySizeFrame) + ChromeApi.GetSystemMetrics(ChromeApi.SmCxPaddedBorder);
                    bool left = cpt.X < fw;
                    bool right = cpt.X >= ClientSize.Width - fw;
                    bool top = cpt.Y < fh;
                    bool bottom = cpt.Y >= ClientSize.Height - fh;
                    if (top && left) { m.Result = (IntPtr)ChromeApi.HtTopLeft; return; }
                    if (top && right) { m.Result = (IntPtr)ChromeApi.HtTopRight; return; }
                    if (bottom && left) { m.Result = (IntPtr)ChromeApi.HtBottomLeft; return; }
                    if (bottom && right) { m.Result = (IntPtr)ChromeApi.HtBottomRight; return; }
                    if (top) { m.Result = (IntPtr)ChromeApi.HtTop; return; }
                    if (bottom) { m.Result = (IntPtr)ChromeApi.HtBottom; return; }
                    if (left) { m.Result = (IntPtr)ChromeApi.HtLeft; return; }
                    if (right) { m.Result = (IntPtr)ChromeApi.HtRight; return; }
                }
                if (_titleBar != null && cpt.Y < _titleBar.Height) { m.Result = (IntPtr)ChromeApi.HtCaption; return; }
                m.Result = (IntPtr)ChromeApi.HtClient;
                return;
            }
            base.WndProc(ref m);
        }

        private void HandleDpiChanged(ref Message m)
        {
            long wparam = m.WParam.ToInt64();
            int dpiX = (int)(wparam & 0xFFFF);
            int dpiY = (int)((wparam >> 16) & 0xFFFF);
            float newScale = Math.Max(dpiX, dpiY) / 96f;
            if (newScale < 0.5f || newScale > 8f) { m.Result = IntPtr.Zero; return; }
            ChromeApi.ChromeRect proposed = (ChromeApi.ChromeRect)Marshal.PtrToStructure(m.LParam, typeof(ChromeApi.ChromeRect));
            try
            {
                Bounds = new Rectangle(proposed.Left, proposed.Top,
                    proposed.Right - proposed.Left, proposed.Bottom - proposed.Top);
            }
            catch { }
            m.Result = IntPtr.Zero;
        }

        // ---------- status bar ----------

        private void BuildStatusBar()
        {
            _statusBar = new Panel();
            _statusBar.Dock = DockStyle.Bottom;
            _statusBar.Height = StatusBarH;
            _statusBar.BackColor = Theme.HeaderBar;
            _content.Controls.Add(_statusBar);

            _lblStatus = new Label();
            _lblStatus.Text = "Ready";
            _lblStatus.ForeColor = Theme.Dim;
            _lblStatus.Font = Theme.Small;
            _lblStatus.AutoSize = false;
            _lblStatus.SetBounds(12, 0, 400, StatusBarH);
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _statusBar.Controls.Add(_lblStatus);

            _lblStatusRight = new Label();
            _lblStatusRight.Text = "No files modified";
            _lblStatusRight.ForeColor = Theme.Faint;
            _lblStatusRight.Font = Theme.Small;
            _lblStatusRight.AutoSize = false;
            _lblStatusRight.TextAlign = ContentAlignment.MiddleRight;
            _statusBar.Controls.Add(_lblStatusRight);
            _statusBar.Resize += delegate
            {
                _lblStatusRight.SetBounds(_statusBar.Width - 264, 0, 250, StatusBarH);
            };
            _lblStatusRight.SetBounds(_statusBar.Width - 264, 0, 250, StatusBarH);
        }

        public void SetStatus(string text)
        {
            _lblStatus.Text = text;
        }

        // ---------- card headers (shared pattern) ----------

        private static void BuildCardHeader(Card card, string glyph, string title, string subtitle)
        {
            IconChip chip = new IconChip(glyph);
            chip.Location = new Point(14, 12);
            card.Controls.Add(chip);

            Label t = new Label();
            t.Text = title;
            t.Font = Theme.CardTitle;
            t.ForeColor = Theme.Light;
            t.SetBounds(62, 10, 340, 24);
            card.Controls.Add(t);

            Label s = new Label();
            s.Text = subtitle;
            s.Font = Theme.CardSubtitle;
            s.ForeColor = Theme.Dim;
            s.SetBounds(62, 36, 460, 18);
            card.Controls.Add(s);
        }

        private static Label MakeDimLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.Regular;
            l.ForeColor = Theme.Dim;
            l.SetBounds(x, y, w, 18);
            return l;
        }

        // ---------- card 1: Download Audio ----------

        private void BuildDownloadCard()
        {
            _pnlDownload = new Card();
            _pnlDownload.SetBounds(12, DownloadY, DefaultW - 24, DownloadCardH);
            _pnlDownload.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _content.Controls.Add(_pnlDownload);
            int w = _pnlDownload.Width;

            BuildCardHeader(_pnlDownload, "\uE896", "Download Audio", "Download music from YouTube (audio only)");

            // Cookie management entry (adapted from the source project's
            // "Cookies..." button): opens the jar state dialog, from which the
            // user can save YouTube cookies from a signed-in browser or delete
            // the saved jar.
            _btnCookies = new RoundedButton();
            _btnCookies.Text = "Cookies...";
            _btnCookies.ButtonStyle = RoundedButton.StyleNeutral;
            _btnCookies.Font = Theme.Small;
            _btnCookies.SetBounds(w - 114, 14, 100, 27);
            _btnCookies.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnCookies.Click += delegate { OnCookiesClick(); };
            _pnlDownload.Controls.Add(_btnCookies);

            _pnlDownload.Controls.Add(MakeDimLabel("YouTube URL", 14, 60, 200));

            // URL input uses the full content width; clipboard paste remains
            // available through the icon inside the field.
            _inUrl = new InputBox();
            _inUrl.SetBounds(14, 80, w - 28, 32);
            _inUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _inUrl.Inner.PlaceholderText = "Paste YouTube link here...";
            _pnlDownload.Controls.Add(_inUrl);
            _inUrl.AddRightButton("\uE8C8", "Paste from clipboard", OnPasteUrl);

            _pnlDownload.Controls.Add(MakeDimLabel("Format", 14, 126, 52));

            // Format selector in the app style (DarkCombo): both options are
            // real yt-dlp modes - "MP3 (recommended)" converts the best audio
            // stream to MP3 via the bundled ffmpeg, "Best available
            // (original)" keeps YouTube's original audio container.
            _cboFormat = new DarkCombo();
            _cboFormat.Font = Theme.Regular;
            _cboFormat.Items = new[] { "MP3 (recommended)", "Best available (original)" };
            _cboFormat.SelectedIndex = 0;
            _cboFormat.SetBounds(74, 120, 240, 30);
            _pnlDownload.Controls.Add(_cboFormat);

            _pnlDownload.Controls.Add(MakeDimLabel("Save to", 14, 150, 200));
            _inSaveTo = new InputBox();
            _inSaveTo.LeftImage = FolderIconResource.Small;
            _inSaveTo.SetBounds(14, 168, w - 28, 32);
            _inSaveTo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _inSaveTo.Inner.ReadOnly = true;
            _inSaveTo.Inner.Cursor = Cursors.Hand;
            _inSaveTo.AccessibleName = "Save to folder";
            _inSaveTo.Inner.AccessibleName = "Save to folder";
            _inSaveTo.Inner.AccessibleDescription = "Press Enter or Space to choose the save folder.";
            _inSaveTo.Inner.Click += delegate { BrowseSaveFolder(); };
            _inSaveTo.Click += delegate { BrowseSaveFolder(); };
            _inSaveTo.Inner.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    e.SuppressKeyPress = true;
                    BrowseSaveFolder();
                }
            };
            _pnlDownload.Controls.Add(_inSaveTo);

            _btnDownload = new RoundedButton();
            _btnDownload.Text = "Download";
            _btnDownload.Glyph = "\uE896";
            _btnDownload.GlyphFont = Theme.Glyph;
            _btnDownload.ButtonStyle = RoundedButton.StyleGreen;
            _btnDownload.Font = Theme.RegularBold;
            _btnDownload.SetBounds(14, 208, 126, 34);
            _btnDownload.Click += OnDownloadClick;
            _pnlDownload.Controls.Add(_btnDownload);

            _btnCancelDownload = new RoundedButton();
            _btnCancelDownload.Text = "Cancel";
            _btnCancelDownload.ButtonStyle = RoundedButton.StyleNeutral;
            _btnCancelDownload.Font = Theme.Regular;
            _btnCancelDownload.SetBounds(150, 208, 100, 34);
            _btnCancelDownload.Enabled = false;
            _btnCancelDownload.Click += delegate { CancelDownload(); };
            _pnlDownload.Controls.Add(_btnCancelDownload);

            _btnOpenSaveFolder = new RoundedButton();
            _btnOpenSaveFolder.Text = "Open Folder";
            _btnOpenSaveFolder.GlyphImage = FolderIconResource.Small;
            _btnOpenSaveFolder.ButtonStyle = RoundedButton.StyleNeutral;
            _btnOpenSaveFolder.Font = Theme.Regular;
            _btnOpenSaveFolder.SetBounds(260, 208, 100, 34);
            _btnOpenSaveFolder.Click += delegate { OpenSaveFolder(); };
            _pnlDownload.Controls.Add(_btnOpenSaveFolder);

            _progress = new ProgressLite();
            _progress.SetBounds(14, 250, w - 28 - 50, 6);
            _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _pnlDownload.Controls.Add(_progress);

            _lblPercent = new Label();
            _lblPercent.Text = "0%";
            _lblPercent.Font = Theme.Small;
            _lblPercent.ForeColor = Theme.Dim;
            _lblPercent.TextAlign = ContentAlignment.MiddleRight;
            _lblPercent.SetBounds(w - 56, 242, 44, 18);
            _lblPercent.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _pnlDownload.Controls.Add(_lblPercent);

            _lblDlStatus = new Label();
            _lblDlStatus.Text = "Ready to download.";
            _lblDlStatus.Font = Theme.Small;
            _lblDlStatus.ForeColor = Theme.Dim;
            _lblDlStatus.SetBounds(14, 262, w - 28, 18);
            _lblDlStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _pnlDownload.Controls.Add(_lblDlStatus);
        }

        private void OnPasteUrl(object sender, EventArgs e)
        {
            try
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _inUrl.Inner.Text = text.Trim();
                    SetStatus("Link pasted from clipboard.");
                    _lblDlStatus.Text = "Link pasted from clipboard.";
                }
                else
                {
                    SetStatus("Clipboard is empty.");
                }
            }
            catch (Exception ex)
            {
                SetStatus("Clipboard is not available: " + ex.Message);
            }
        }

        private void BrowseSaveFolder()
        {
            string initial = _inSaveTo.Inner.Text.Trim();
            if ((initial.Length == 0 || !Directory.Exists(initial)) && !string.IsNullOrEmpty(_settings.LastFolder))
                initial = _settings.LastFolder;

            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the folder to save downloaded audio";
                dialog.UseDescriptionForTitle = true;
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                    dialog.SelectedPath = initial;
                dialog.ShowNewFolderButton = true;
                dialog.AutoUpgradeEnabled = true;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string picked = dialog.SelectedPath;
                if (string.IsNullOrEmpty(picked)) return;

                _inSaveTo.Inner.Text = picked;
                ShowPathTail(_inSaveTo);
                SaveSettings();
                SetStatus("Save folder: " + picked);
            }
        }

        private void OpenSaveFolder()
        {
            string folder = _inSaveTo.Inner.Text.Trim();
            if (folder.Length == 0)
            {
                DarkDialog.ShowInfo(this, "Save folder not selected",
                    "Choose a folder in the Save to field first.", null);
                SetStatus("Save folder is not selected.");
                return;
            }
            if (!Directory.Exists(folder))
            {
                DarkDialog.ShowInfo(this, "Save folder not found",
                    "The selected save folder does not exist:\n" + folder, null);
                SetStatus("Save folder not found.");
                return;
            }

            try
            {
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = "explorer.exe";
                start.UseShellExecute = true;
                start.ArgumentList.Add(folder);
                Process.Start(start);
                SetStatus("Opened save folder.");
            }
            catch (Exception ex)
            {
                DarkDialog.ShowInfo(this, "Could not open folder",
                    "Windows Explorer could not open the selected save folder.\n\n" + ex.Message, null);
                SetStatus("Could not open save folder.");
            }
        }

        // ---------- cookies (adapted from YouTubeDownloader-Net10) ----------

        private void OnCookiesClick()
        {
            if (_downloading || _checking || _cookieBusy)
            {
                SetStatus("Wait until the current download or check finishes.");
                return;
            }
            // The dialog owns no cookie logic: it reports the jar state and the
            // user's chosen action (close / get-from-browser).
            int action = CookiesDialog.Show(this, _settings);
            if (action != CookiesDialog.ActionGetFromBrowser) return;
            PrepareCookiesFromBrowser();
        }

        // Offers installed supported browsers and runs ONE yt-dlp export fully
        // offline (no URL): yt-dlp itself reads the selected browser profile and
        // writes the jar next to the app. Cookie values are never read, shown
        // or logged by this application (only cookie NAMES are inspected to
        // verify the jar; see CookieStore).
        private void PrepareCookiesFromBrowser()
        {
            List<BrowserInfo> installed = BrowserCookies.InstalledSupported();
            if (installed.Count == 0)
            {
                DiagLog.Event("cookies", "no supported browser profile found");
                DarkDialog.ShowInfo(this, "No supported browser found",
                    "No browser profile for saving YouTube cookies was found on this computer.\n\n"
                    + "Sign in to youtube.com in Firefox, Chrome, Edge, Brave, Vivaldi or Opera, "
                    + "then try saving cookies again.", null);
                return;
            }
            string key = BrowserPickDialog.Show(this, installed, BrowserCookies.DetectDefaultKey());
            if (key == null) return;

            _cookieBusy = true;
            _btnCookies.Enabled = false;
            SetStatus("Saving YouTube cookies from " + BrowserCookies.DisplayForKey(key) + "...");
            _lblDlStatus.ForeColor = Theme.Dim;
            _lblDlStatus.Text = "Saving YouTube cookies from " + BrowserCookies.DisplayForKey(key) + "...";

            Task<CookiePrepareResult> task = CookieStore.PrepareFromBrowserAsync(key, null);
            task.ContinueWith(t =>
            {
                _cookieBusy = false;
                if (IsDisposed) return;
                _btnCookies.Enabled = true;
                _lblDlStatus.ForeColor = Theme.Dim;
                CookiePrepareResult r = (t.IsFaulted || t.Result == null) ? null : t.Result;
                if (r != null && r.Success)
                {
                    _settings.CookiesSourceBrowser = r.Browser;
                    _settings.Save();
                    if (r.LooksSignedIn)
                    {
                        _lblDlStatus.ForeColor = Theme.GreenText;
                        _lblDlStatus.Text = "YouTube cookies saved.";
                        SetStatus("YouTube cookies saved from " + BrowserCookies.DisplayForKey(r.Browser) + ".");
                    }
                    else
                    {
                        // Kept: an unsign-in jar is still useful for bot checks.
                        SetStatus("Cookies saved, but no YouTube sign-in session found in the jar.");
                        DarkDialog.ShowInfo(this, "Cookies saved",
                            "The cookies were saved, but they may not include a YouTube sign-in session.\n\n"
                            + "If YouTube still requires sign-in, make sure the selected browser is signed in "
                            + "to youtube.com and save the cookies again.",
                            null);
                    }
                }
                else
                {
                    string reason = (r != null && !string.IsNullOrEmpty(r.Error)) ? r.Error : "unknown error";
                    string browserName = BrowserCookies.DisplayForKey(
                        r != null && !string.IsNullOrEmpty(r.Browser) ? r.Browser : key);
                    DiagLog.Event("cookies", "export reported failure for " + key + ": " + reason);
                    _lblDlStatus.ForeColor = Theme.Red;
                    _lblDlStatus.Text = "Could not save YouTube cookies.";
                    DarkDialog.ShowInfo(this, "Could not save cookies",
                        "YouTube cookies could not be exported from " + browserName + ".\n\n"
                        + reason
                        + "\n\nMake sure the browser is installed and has a profile it can read, "
                        + "and close the browser fully before exporting.",
                        null);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------- Stage 2: download flow ----------

        private void OnDownloadClick(object sender, EventArgs e)
        {
            if (_downloading) return;
            if (_checking) return;

            // 1. Tools must be present next to the EXE.
            string missing = AppPaths.MissingCoreComponent();
            if (missing != null)
            {
                DarkDialog.ShowInfo(this, "Download and Search Audio",
                    "A required tool is missing next to the application:\n" + missing,
                    null);
                SetStatus("Missing tool: " + missing);
                return;
            }

            // 2. URL must be present and valid before anything is started.
            string url = _inUrl.Inner.Text.Trim();
            if (url.Length == 0)
            {
                SetStatus("Type or paste a YouTube link first.");
                _lblDlStatus.Text = "Type or paste a YouTube link first.";
                return;
            }
            if (!YouTubeUrl.IsValid(url))
            {
                DarkDialog.ShowInfo(this, "Invalid link",
                    "The text in the YouTube URL field is not a valid YouTube link.", null);
                SetStatus("Invalid YouTube URL.");
                return;
            }

            // 3. Save folder must exist. A missing folder blocks the download.
            string folder = _inSaveTo.Inner.Text.Trim();
            if (folder.Length == 0)
            {
                DarkDialog.ShowInfo(this, "Save folder not selected",
                    "Choose an existing folder to save the downloaded audio.", null);
                SetStatus("Save folder is not selected.");
                return;
            }
            if (!Directory.Exists(folder))
            {
                DarkDialog.ShowInfo(this, "Save folder not found",
                    "The save folder does not exist:\n" + folder + "\n\nChoose an existing folder.",
                    null);
                SetStatus("Save folder not found.");
                return;
            }

            // 4. Probe the video title WITHOUT downloading (yt-dlp --print).
            AudioOutputMode mode = _cboFormat.SelectedIndex == 0 ? AudioOutputMode.Mp3 : AudioOutputMode.Original;
            _checking = true;
            _btnDownload.Enabled = false;
            _btnDownload.Text = "Checking...";
            _btnCancelDownload.Enabled = false;
            _btnCookies.Enabled = false;
            SetStatus("Getting video info...");
            _lblDlStatus.Text = "Getting video info...";

            Task.Run(delegate
            {
                YtDlpRunner.AudioTitleInfo info;
                string output;
                bool ok = YtDlpRunner.TryGetAudioTitle(url, out info, out output);
                return new { ok, info, output };
            })
            .ContinueWith(t =>
            {
                var probe = t.Result;
                _checking = false;
                _btnCookies.Enabled = true;
                if (IsDisposed) return;

                if (!probe.ok)
                {
                    EngineFailureKind kind = YtDlpRunner.Classify(probe.output);
                    _btnDownload.Enabled = true;
                    _btnDownload.Text = "Download";
                    _lblDlStatus.ForeColor = Theme.Red;
                    _lblDlStatus.Text = "Could not get the video title. Download is not started.";
                    SetStatus("Could not get the video title: " + YtDlpRunner.FailureText(kind));
                    // Honest, bounded user message: name the access problem and
                    // mention cookies only as a possible help - never as a
                    // guaranteed fix. Technical details go to the diagnostic
                    // log; the dialog shows only the sanitized ERROR lines.
                    bool noCookies = !CookieStore.HasUsableCookies();
                    string tip = noCookies
                        && (kind == EngineFailureKind.BotCheck || kind == EngineFailureKind.RateLimited
                            || kind == EngineFailureKind.AccessDenied)
                        ? "\n\nTip: no saved YouTube cookies are configured. Click the Cookies button to save signed-in YouTube cookies from one of your browsers, then try again."
                        : "";
                    DarkDialog.ShowInfo(this, "Download not started",
                        "Could not get the video title from YouTube, so the required duplicate check cannot run.\n\n"
                        + YtDlpRunner.FailureText(kind) + tip,
                        ErrorDetailLines(probe.output));
                    return;
                }
                _btnDownload.Enabled = true;
                _btnDownload.Text = "Download";
                _lblDlStatus.ForeColor = Theme.Dim;

                // 5. REQUIRED pre-download duplicate check over the actual save
                // folder (recursive). Any uncertainty stops the download.
                DuplicateCheckResult dup = DuplicateCheck.CheckFolder(folder, probe.info.Title, true);
                switch (dup.Status)
                {
                    case DuplicateCheckStatus.FolderNotSpecified:
                    case DuplicateCheckStatus.FolderMissing:
                        DarkDialog.ShowInfo(this, "Save folder not found",
                            "The save folder does not exist:\n" + folder + "\n\nChoose an existing folder.",
                            null);
                        SetStatus("Save folder not found. Download is not started.");
                        return;
                    case DuplicateCheckStatus.ScanError:
                        DarkDialog.ShowInfo(this, "Folder check failed",
                            "The save folder could not be checked for existing files, so the download is not started.\n\n"
                            + dup.Error,
                            null);
                        SetStatus("Save folder check failed. Download is not started.");
                        return;
                    case DuplicateCheckStatus.MatchesFound:
                        // Confirmed duplicate: STOP. Never auto-continue, never
                        // overwrite or delete the existing file.
                        _lblDlStatus.ForeColor = Theme.Orange;
                        _lblDlStatus.Text = "File already exists. Download stopped.";
                        SetStatus("File already exists. Download stopped.");
                        DarkDialog.ShowDuplicateWarning(this, dup.MatchPaths.ToArray());
                        return;
                }

                // 6. No duplicates: start the download.
                _lblDlStatus.ForeColor = Theme.Dim;
                StartDownload(url, folder, mode, probe.info.Title);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static string[] LastLines(string text, int count)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] all = text.Replace("\r", "").Split('\n');
            List<string> lines = new List<string>();
            for (int i = Math.Max(0, all.Length - count); i < all.Length; i++)
                if (all[i].Trim().Length > 0) lines.Add(all[i].Trim());
            return lines.Count > 0 ? lines.ToArray() : null;
        }

        // Short, safe detail for user-facing dialogs: up to 3 sanitized lines
        // yt-dlp itself marked as ERROR (private data and URL secrets are
        // redacted by DiagnosticSanitizer). Full sanitized output goes to the
        // diagnostic log (DiagLog), so a raw dump is never needed in the UI.
        private static string[] ErrorDetailLines(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            string[] all = output.Replace("\r", "").Split('\n');
            List<string> errors = new List<string>();
            for (int i = all.Length - 1; i >= 0 && errors.Count < 3; i--)
            {
                string s = all[i].Trim();
                if (s.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) errors.Add(DiagnosticSanitizer.Sanitize(s));
            }
            errors.Reverse();
            if (errors.Count > 0) return errors.ToArray();
            // Fall back to the last non-empty sanitized line, trimmed to one line.
            for (int i = all.Length - 1; i >= 0; i--)
            {
                string s = DiagnosticSanitizer.Sanitize(all[i].Trim());
                if (s.Length > 0) return new[] { s.Length > 220 ? s.Substring(0, 220) + "..." : s };
            }
            return null;
        }

        private void StartDownload(string url, string folder, AudioOutputMode mode, string title)
        {
            _cancelRequested = false;
            _downloadDest = null;
            _extractDest = null;
            _downloadStartUtc = DateTime.UtcNow;
            _progress.Value = 0;
            _lblPercent.Text = "0%";
            _lblDlStatus.ForeColor = Theme.Dim;
            _lblDlStatus.Text = "Downloading: " + title;
            SetStatus("Downloading: " + title);
            RunDownloadAttempt(url, folder, mode, title, null);
        }

        // Runs one yt-dlp download attempt. cookieFile != null marks the single
        // reserve attempt: the ONLY difference versus the plain attempt is the
        // added "--cookies <staged copy>" (same invariant as the source
        // project's DownloadArgs), so the two attempts are strictly comparable.
        private void RunDownloadAttempt(string url, string folder, AudioOutputMode mode, string title, string cookieFile)
        {
            _downloading = true;
            _btnDownload.Text = "Downloading...";
            _btnDownload.Enabled = false;
            _btnCancelDownload.Enabled = true;
            _btnCookies.Enabled = false;

            string[] args = YtDlpRunner.AudioDownloadArgs(folder, url, mode);
            if (!string.IsNullOrEmpty(cookieFile)) args = YtDlpRunner.WithCookies(args, cookieFile);

            YtDlpRunner.RunAsync(args, OnYtLine, delegate(Process p) { _ytProc = p; })
                .ContinueWith(t =>
                {
                    _downloading = false;
                    _ytProc = null;
                    DisposeStagedCookie();
                    _btnCookies.Enabled = true;
                    if (IsDisposed) return;
                    _btnDownload.Text = "Download";
                    _btnDownload.Enabled = true;
                    _btnCancelDownload.Enabled = false;
                    if (t.IsFaulted)
                    {
                        _lblDlStatus.ForeColor = Theme.Red;
                        _lblDlStatus.Text = "Failed to start the downloader.";
                        SetStatus("Failed to start yt.exe.");
                        return;
                    }
                    FinishDownload(t.Result, url, folder, mode, title, cookieFile);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void DisposeStagedCookie()
        {
            StagedCookieFile stage = _stagedCookie;
            _stagedCookie = null;
            if (stage != null) stage.Dispose();
        }

        private void OnYtLine(YtLine line)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(delegate { OnYtLine(line); }));
                return;
            }
            string text = line.Text ?? "";
            double? pct = OutputParser.TryPercent(text);
            if (pct.HasValue)
            {
                _progress.Value = pct.Value;
                _lblPercent.Text = Math.Round(pct.Value) + "%";
                string speed = OutputParser.TrySpeed(text);
                string eta = OutputParser.TryEta(text);
                _lblDlStatus.Text = "Downloading: " + titleShort() + (speed != null ? "  at " + speed : "") + (eta != null ? "  ETA " + eta : "");
            }
            else if (OutputParser.IsFetchingInfo(text))
            {
                _lblDlStatus.Text = "Downloading: fetching info...";
            }
            string dest = OutputParser.TryDestination(text);
            if (dest != null) _downloadDest = dest;
            string extract = OutputParser.TryExtractAudioDestination(text);
            if (extract != null)
            {
                _extractDest = extract;
                _lblDlStatus.Text = "Converting to MP3...";
            }
            if (OutputParser.IsAlreadyDownloaded(text))
            {
                _lblDlStatus.Text = "File already downloaded earlier.";
            }
        }

        private string titleShort()
        {
            string t = _lblDlStatus.Text;
            int i = t.IndexOf("  at ", StringComparison.Ordinal);
            if (i > 0) t = t.Substring(0, i);
            int j = t.IndexOf("Downloading: ", StringComparison.Ordinal);
            return j == 0 ? t.Substring(13) : t;
        }

        private void FinishDownload(YtRunResult result, string url, string folder, AudioOutputMode mode, string title, string cookieFile)
        {
            if (_cancelRequested)
            {
                // Remove artifacts of THIS canceled run (.part, downloaded source,
                // half-converted output). Only files matching the title AND
                // created after the download started are touched — pre-existing
                // user files are never modified.
                Task.Run(delegate { CleanupCanceledArtifacts(folder, title); });
                _progress.Value = 0;
                _lblPercent.Text = "0%";
                _lblDlStatus.ForeColor = Theme.Dim;
                _lblDlStatus.Text = "Download canceled.";
                SetStatus("Download canceled.");
                return;
            }

            if (result.ExitCode == 0)
            {
                // Prove the output file really exists before claiming success.
                string finalPath = ResolveOutputFile(folder, title);
                if (finalPath != null)
                {
                    _progress.Value = 100;
                    _lblPercent.Text = "100%";
                    _lblDlStatus.ForeColor = Theme.GreenText;
                    _lblDlStatus.Text = "Saved: " + Path.GetFileName(finalPath);
                    SetStatus("Saved: " + finalPath);
                    return;
                }
                _lblDlStatus.ForeColor = Theme.Red;
                _lblDlStatus.Text = "Download finished, but the output file was not found.";
                SetStatus("Output file not found after download.");
                DarkDialog.ShowInfo(this, "Output not found",
                    "The downloader reported success, but the audio file was not found in the save folder.",
                    null);
                return;
            }

            EngineFailureKind kind = YtDlpRunner.Classify(result.Output);
            DiagLog.Event("download", "exit: code=" + result.ExitCode
                + " category=" + kind
                + " attempt=" + (cookieFile != null ? "cookies" : "anonymous")
                + "\n" + result.Output);

            // Single reserve attempt with the saved cookie jar (source policy,
            // VideoDownloadEngine.DownloadOrchestrator): exactly ONE extra
            // strictly sequential run, made only when the first attempt never
            // carried cookies, the failure is one cookies can plausibly fix,
            // and a usable jar exists. A bare transient 429 WITHOUT an
            // authentication signal does NOT trigger it.
            if (cookieFile == null
                && YtDlpRunner.CookiesMayHelpDownload(kind, result.Output)
                && !string.IsNullOrEmpty(CookieStore.UsableFile()))
            {
                StagedCookieFile staged = CookieStore.StageUsableCopy();
                if (staged != null)
                {
                    _stagedCookie = staged;
                    _progress.Value = 0;
                    _lblPercent.Text = "0%";
                    _downloadDest = null;
                    _extractDest = null;
                    _downloadStartUtc = DateTime.UtcNow;
                    _lblDlStatus.ForeColor = Theme.Orange;
                    _lblDlStatus.Text = "Retrying with saved YouTube cookies...";
                    SetStatus("Retrying with saved YouTube cookies...");
                    RunDownloadAttempt(url, folder, mode, title, staged.FilePath);
                    return;
                }
                DiagLog.Event("download", "reserve attempt skipped: a staged cookie copy could not be prepared");
            }

            _lblDlStatus.ForeColor = Theme.Red;
            _lblDlStatus.Text = YtDlpRunner.FailureText(kind);
            SetStatus("Download failed: " + YtDlpRunner.FailureText(kind));
            bool noCookies = !CookieStore.HasUsableCookies()
                && (kind == EngineFailureKind.BotCheck
                    || kind == EngineFailureKind.RateLimited
                    || kind == EngineFailureKind.AccessDenied);
            string tip = noCookies
                ? "\n\nTip: no saved YouTube cookies are configured. Click the Cookies button to save signed-in YouTube cookies from one of your browsers, then try again."
                : "";
            DarkDialog.ShowInfo(this, "Download failed",
                YtDlpRunner.FailureText(kind) + tip,
                ErrorDetailLines(result.Output));
        }

        // Finds the produced audio file: prefer the paths yt-dlp itself printed
        // (download destination / extract-audio destination), fall back to the
        // newest matching audio file in the save folder created during this run.
        private string ResolveOutputFile(string folder, string title)
        {
            if (_extractDest != null && File.Exists(_extractDest)) return _extractDest;
            if (_downloadDest != null && File.Exists(_downloadDest)) return _downloadDest;
            try
            {
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true
                }))
                {
                    if (!DuplicateCheck.AudioExtensions.Contains(Path.GetExtension(path))) continue;
                    if (!DuplicateCheck.IsMatch(Path.GetFileNameWithoutExtension(path), title)) continue;
                    DateTime wt = File.GetLastWriteTimeUtc(path);
                    if (wt >= _downloadStartUtc.AddSeconds(-2) && wt > bestTime)
                    {
                        bestTime = wt;
                        best = path;
                    }
                }
                return best;
            }
            catch
            {
                return null;
            }
        }

        private void CleanupCanceledArtifacts(string folder, string title)
        {
            try
            {
                DateTime cutoff = _downloadStartUtc.AddSeconds(-2);
                foreach (string path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true
                }))
                {
                    string ext = Path.GetExtension(path);
                    string nameNoExt = Path.GetFileNameWithoutExtension(path);
                    bool isPart = string.Equals(ext, ".part", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
                    bool matchesTitle = isPart
                        ? DuplicateCheck.IsMatch(Path.GetFileNameWithoutExtension(nameNoExt), title)
                        : DuplicateCheck.IsMatch(nameNoExt, title);
                    if (!matchesTitle) continue;
                    bool createdThisRun;
                    try { createdThisRun = File.GetLastWriteTimeUtc(path) >= cutoff; }
                    catch { continue; }
                    if (!createdThisRun) continue;
                    try { File.Delete(path); } catch { }
                }
            }
            catch { }
        }

        private void CancelDownload()
        {
            if (!_downloading || _cancelRequested) return;
            _cancelRequested = true;
            _btnCancelDownload.Enabled = false;
            _lblDlStatus.Text = "Canceling...";
            SetStatus("Canceling the download...");
            YtDlpRunner.KillTree(_ytProc);
        }

        // ---------- card 2: Music Search ----------

        private void BuildSearchCard()
        {
            _pnlSearch = new Card();
            _pnlSearch.SetBounds(12, SearchY, DefaultW - 24, SearchCardH);
            _pnlSearch.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _content.Controls.Add(_pnlSearch);
            int w = _pnlSearch.Width;

            BuildCardHeader(_pnlSearch, ((char)0xE721).ToString(), "Music Search", "Find an audio file by name");

            Label lblFolder = MakeDimLabel("Search folder", 14, 62, 100);
            lblFolder.TextAlign = ContentAlignment.MiddleLeft;
            lblFolder.Height = 32;
            _pnlSearch.Controls.Add(lblFolder);

            _chkSubfolders = new CheckBox();
            _chkSubfolders.Text = "Include subfolders";
            _chkSubfolders.Font = Theme.Regular;
            _chkSubfolders.ForeColor = Theme.Light;
            _chkSubfolders.AutoSize = false;
            _chkSubfolders.Checked = true;
            _chkSubfolders.SetBounds(w - 14 - 148, 66, 148, 24);
            _chkSubfolders.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _chkSubfolders.CheckedChanged += delegate { SaveSettings(); };
            _pnlSearch.Controls.Add(_chkSubfolders);

            RoundedButton btnChange = new RoundedButton();
            btnChange.Text = "Change...";
            btnChange.ButtonStyle = RoundedButton.StyleNeutral;
            btnChange.Font = Theme.Regular;
            btnChange.SetBounds(w - 14 - 148 - 8 - 96, 62, 96, 32);
            btnChange.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnChange.Click += delegate { BrowseSearchFolder(); };
            _pnlSearch.Controls.Add(btnChange);

            _inSearchFolder = new InputBox();
            _inSearchFolder.LeftImage = FolderIconResource.Small;
            _inSearchFolder.SetBounds(118, 62, btnChange.Left - 10 - 118, 32);
            _inSearchFolder.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _inSearchFolder.Inner.PlaceholderText = "No folder selected";
            _inSearchFolder.Inner.ReadOnly = true;
            _inSearchFolder.Inner.Cursor = Cursors.Hand;
            _inSearchFolder.Inner.Click += delegate { BrowseSearchFolder(); };
            _pnlSearch.Controls.Add(_inSearchFolder);

            _btnSearch = new RoundedButton();
            _btnSearch.Text = "Search";
            _btnSearch.ButtonStyle = RoundedButton.StyleGreen;
            _btnSearch.Font = Theme.RegularBold;
            _btnSearch.SetBounds(w - 14 - 120, 102, 120, 32);
            _btnSearch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnSearch.Click += OnSearchClick;
            _pnlSearch.Controls.Add(_btnSearch);

            _inQuery = new InputBox();
            _inQuery.LeftGlyph = ((char)0xE721).ToString();
            _inQuery.SetBounds(14, 102, _btnSearch.Left - 10 - 14, 32);
            _inQuery.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _inQuery.Inner.PlaceholderText = "Enter a file name or part of it...";
            _inQuery.Inner.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                OnSearchClick(_btnSearch, EventArgs.Empty);
            };
            _pnlSearch.Controls.Add(_inQuery);

            string[] headers = { "Title", "Extension", "Size", "Path", "Duration" };
            int rowWidth = w - 28 - SystemInformation.VerticalScrollBarWidth;
            int[] columnWidths = SearchResultColumnWidths(rowWidth);
            _searchResultHeaders = new Label[headers.Length];
            int columnX = 14;
            for (int i = 0; i < headers.Length; i++)
            {
                Label header = new Label();
                header.Text = headers[i];
                header.Font = Theme.Small;
                header.ForeColor = Theme.Dim;
                header.BackColor = Theme.Card;
                header.TextAlign = ContentAlignment.MiddleLeft;
                header.AutoEllipsis = true;
                header.SetBounds(columnX + 6, 140, columnWidths[i] - 12, 16);
                _pnlSearch.Controls.Add(header);
                _searchResultHeaders[i] = header;
                columnX += columnWidths[i];
            }

            _searchResult = new SearchResultsViewport();
            _searchResult.SetBounds(14, 156, w - 28, 136);
            _searchResult.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _pnlSearch.Controls.Add(_searchResult);

            _searchStatus = new Label();
            _searchStatus.Font = Theme.Regular;
            _searchStatus.ForeColor = Theme.Dim;
            _searchStatus.BackColor = Theme.Input;
            _searchStatus.TextAlign = ContentAlignment.MiddleLeft;
            _searchStatus.Padding = new Padding(8, 0, 8, 0);
            _searchStatus.SetBounds(8, 0, rowWidth - 16, 32);
            _searchStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _searchStatus.Visible = false;
            _searchResult.Controls.Add(_searchStatus);
        }

        private static int[] SearchResultColumnWidths(int totalWidth)
        {
            int[] baseline = { 170, 84, 85, 241, 88 };
            int baselineTotal = 0;
            for (int i = 0; i < baseline.Length; i++) baselineTotal += baseline[i];

            int dataWidth = totalWidth - PreviewButtonSize * 2 - PreviewButtonGap - PreviewRightPadding;

            int[] widths = new int[baseline.Length];
            int assigned = 0;
            for (int i = 0; i < widths.Length - 1; i++)
            {
                widths[i] = (int)Math.Round((double)baseline[i] * dataWidth / baselineTotal);
                assigned += widths[i];
            }
            widths[widths.Length - 1] = dataWidth - assigned;
            return widths;
        }
        private void BrowseSearchFolder()
        {
            string initial = _inSearchFolder.Inner.Text.Trim();
            if ((initial.Length == 0 || !Directory.Exists(initial)) && !string.IsNullOrEmpty(_settings.SearchFolder))
                initial = _settings.SearchFolder;

            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the music folder to search";
                dialog.UseDescriptionForTitle = true;
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                    dialog.SelectedPath = initial;
                dialog.ShowNewFolderButton = false;
                dialog.AutoUpgradeEnabled = true;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string picked = dialog.SelectedPath;
                if (string.IsNullOrEmpty(picked)) return;

                _inSearchFolder.Inner.Text = picked;
                ShowPathTail(_inSearchFolder);
                SaveSettings();
                SetStatus("Search folder: " + picked);
            }
        }

        private async void OnSearchClick(object sender, EventArgs e)
        {
            // Clear the previous answer before validating or starting the next scan.
            ClearSearchResult();

            string query = _inQuery.Inner.Text.Trim();
            if (query.Length == 0)
            {
                ShowSearchStatus("Enter a file name to search.");
                SetStatus("Enter a file name to search.");
                return;
            }

            string folder = _inSearchFolder.Inner.Text.Trim();
            if (folder.Length == 0)
            {
                ShowSearchStatus("Select a search folder.");
                SetStatus("Search folder is not selected.");
                return;
            }

            bool includeSubfolders = _chkSubfolders.Checked;
            _btnSearch.Enabled = false;
            ShowSearchStatus("Searching...");
            SetStatus("Searching for audio files...");
            try
            {
                AudioFileSearchResult result = await Task.Run(delegate
                {
                    return AudioFileSearch.Search(folder, query, includeSubfolders);
                });
                if (IsDisposed) return;

                switch (result.Status)
                {
                    case AudioFileSearchStatus.Found:
                    case AudioFileSearchStatus.MultipleMatches:
                        List<string[]> rows = await Task.Run(delegate
                        {
                            List<string[]> foundRows = new List<string[]>();
                            foreach (string path in result.FilePaths)
                            {
                                FileInfo file = new FileInfo(path);
                                if (!file.Exists) continue;

                                TimeSpan measured;
                                bool hasDuration = AudioDurationProbe.TryGetDuration(AppPaths.FfprobeExe, path, out measured);
                                string format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                                foundRows.Add(new[]
                                {
                                    Path.GetFileNameWithoutExtension(path),
                                    format,
                                    UiFormat.Size(file.Length),
                                    file.FullName,
                                    hasDuration ? FormatAudioDuration(measured) : "—"
                                });
                            }
                            return foundRows;
                        });
                        if (IsDisposed) return;
                        if (rows.Count == 0)
                        {
                            ShowSearchStatus("File not found");
                            SetStatus("File not found.");
                            break;
                        }
                        ShowSearchRows(rows);
                        SetStatus(rows.Count == 1
                            ? "One matching audio file found."
                            : rows.Count.ToString(CultureInfo.InvariantCulture) + " matching audio files found.");
                        break;

                    case AudioFileSearchStatus.EmptyQuery:
                        ShowSearchStatus("Enter a file name to search.");
                        SetStatus("Enter a file name to search.");
                        break;
                    case AudioFileSearchStatus.FolderUnavailable:
                        ShowSearchStatus("Search folder is unavailable or cannot be accessed.");
                        SetStatus("Search folder is unavailable.");
                        break;
                    case AudioFileSearchStatus.ScanError:
                        ShowSearchStatus("Could not read the search folder.");
                        SetStatus("Music Search could not read the selected folder.");
                        break;
                    default:
                        ShowSearchStatus("File not found");
                        SetStatus("File not found.");
                        break;
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    ShowSearchStatus("Search failed. Check that the folder is accessible.");
                    SetStatus("Music Search failed: " + ex.Message);
                }
            }
            finally
            {
                if (!IsDisposed) _btnSearch.Enabled = true;
            }
        }

        private void ClearSearchResult()
        {
            _ = _audioPreview.StopAsync();
            for (int i = 0; i < _searchResultHeaders.Length; i++)
            {
                _searchResultHeaders[i].Visible = false;
            }
            _searchResult.SuspendLayout();
            for (int i = _searchResultRows.Count - 1; i >= 0; i--)
            {
                InputBox row = _searchResultRows[i];
                _searchResult.Controls.Remove(row);
                row.Dispose();
            }
            _searchResultRows.Clear();
            _searchResult.ResetResults();
            _searchResult.ResumeLayout(true);
            _searchStatus.Text = "";
            _searchStatus.Visible = false;
        }

        private void ShowSearchStatus(string text)
        {
            ClearSearchResult();
            _searchStatus.Text = text ?? "";
            _searchStatus.Visible = true;
        }

        private void ShowSearchRows(IReadOnlyList<string[]> rows)
        {
            _searchStatus.Text = "";
            _searchStatus.Visible = false;
            for (int i = 0; i < _searchResultHeaders.Length; i++)
                _searchResultHeaders[i].Visible = true;

            int rowWidth = _searchResult.Width - SystemInformation.VerticalScrollBarWidth;
            int[] columnWidths = SearchResultColumnWidths(rowWidth);
            const int rowHeight = 32;
            const int rowGap = 3;

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                InputBox row = new InputBox();
                row.SetBounds(0, rowIndex * (rowHeight + rowGap), rowWidth, rowHeight);
                row.Inner.Visible = false;

                int columnX = 0;
                for (int column = 0; column < _searchResultHeaders.Length; column++)
                {
                    string value = rows[rowIndex] != null && column < rows[rowIndex].Length
                        ? rows[rowIndex][column] ?? "" : "";
                    Label cell = new Label();
                    cell.Font = Theme.Small;
                    cell.ForeColor = Theme.Light;
                    cell.BackColor = Theme.Input;
                    cell.TextAlign = ContentAlignment.MiddleLeft;
                    cell.AutoEllipsis = true;
                    cell.Padding = new Padding(6, 0, 4, 0);
                    cell.SetBounds(columnX, 0, columnWidths[column], rowHeight);
                    cell.Text = value;
                    cell.AccessibleDescription = value;
                    row.Controls.Add(cell);
                    columnX += columnWidths[column];
                }

                string audioPath = rows[rowIndex] != null && rows[rowIndex].Length > 3
                    ? rows[rowIndex][3] : "";
                AudioTransportButton play = new AudioTransportButton(AudioTransportButton.ActionKind.Play);
                play.SetBounds(columnX + 1, 2, PreviewButtonSize, PreviewButtonSize);
                play.AudioPath = audioPath;
                play.AccessibleName = "Play " + Path.GetFileName(audioPath);
                play.Text = play.AccessibleName;
                play.Click += delegate { PlayAudioPreview(audioPath); };
                row.Controls.Add(play);

                AudioTransportButton stop = new AudioTransportButton(AudioTransportButton.ActionKind.Stop);
                stop.SetBounds(columnX + PreviewButtonSize + PreviewButtonGap + 1, 2,
                    PreviewButtonSize, PreviewButtonSize);
                stop.AccessibleName = "Stop preview for " + Path.GetFileName(audioPath);
                stop.Text = stop.AccessibleName;
                stop.Click += delegate { _ = _audioPreview.StopAsync(); };
                row.Controls.Add(stop);

                _searchResult.AddResultRow(row, rowIndex * (rowHeight + rowGap));
                _searchResultRows.Add(row);
            }

            _searchResult.SetContentHeight(Math.Max(0, rows.Count * (rowHeight + rowGap) - rowGap));
        }

        private static string FormatAudioDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
                return ((int)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + duration.ToString(@"mm\:ss");
            return duration.ToString(@"m\:ss");
        }

        private async void PlayAudioPreview(string path)
        {
            await _audioPreview.PlayAsync(path);
        }

        private void OnPreviewPlaybackFailed(string path, Exception error)
        {
            if (IsDisposed || !IsHandleCreated) return;
            Action showError = delegate
            {
                if (IsDisposed) return;
                string name = Path.GetFileName(path);
                SetStatus("Could not play audio: " + name);
                DarkDialog.ShowInfo(this, "Could not play audio",
                    "The selected audio file could not be opened or decoded.",
                    new[] { name, error.Message });
            };

            if (InvokeRequired)
            {
                try { BeginInvoke(showError); } catch (InvalidOperationException) { }
            }
            else
            {
                showError();
            }
        }

        private void OnPreviewPlaybackChanged(string path, bool isPlaying)
        {
            if (IsDisposed || !IsHandleCreated) return;
            Action update = delegate
            {
                if (IsDisposed) return;
                foreach (InputBox row in _searchResultRows)
                {
                    foreach (Control child in row.Controls)
                    {
                        AudioTransportButton button = child as AudioTransportButton;
                        if (button == null || button.AudioPath == null) continue;
                        button.IsActive = isPlaying
                            && string.Equals(button.AudioPath, path, StringComparison.OrdinalIgnoreCase);
                    }
                }
            };

            if (InvokeRequired)
            {
                try { BeginInvoke(update); } catch (InvalidOperationException) { }
            }
            else
            {
                update();
            }
        }
    }
}
