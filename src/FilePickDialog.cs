using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DownloadSearchAudio
{
    // Shared byte-size formatter (main window and dialogs).
    internal static class UiFormat
    {
        public static string Size(long bytes)
        {
            double b = bytes;
            if (b >= 1024L * 1024 * 1024) return (b / (1024.0 * 1024 * 1024)).ToString("F1") + " GB";
            if (b >= 1024L * 1024) return (b / (1024.0 * 1024)).ToString("F1") + " MB";
            if (b >= 1024) return (b / 1024.0).ToString("F1") + " KB";
            return bytes + " B";
        }
    }

    // App-styled replacement for the system FolderBrowserDialog and
    // OpenFileDialog (user feedback 2026-10-26: the light system dialogs
    // clash with the dark application). Real navigation over drives and
    // directories, plus real file picking with an extension filter.
    //
    // Threading: every listing runs on a worker thread with a generation
    // counter and a CancellationToken, so an unresponsive (e.g. network)
    // path can never block the UI thread: the user keeps navigating freely
    // and stale results are dropped. Enumeration uses IgnoreInaccessible +
    // ReparsePoint skip (the same pattern as the music search), and access
    // errors surface as a note under the list, not as an exception or hang.
    public class DarkBrowseDialog : Form
    {
        public enum PickMode { Folders, Files }

        private sealed class Entry
        {
            public string Display;
            public string FullPath;   // null = not usable (drive not ready)
            public bool IsFolder;     // navigable directory or ready drive
            public long? SizeBytes;   // files only
            public string TypeText;
        }

        private sealed class Listing
        {
            public int Generation;
            public bool DrivesView;
            public List<Entry> Items;
            public string Error;      // access/partial note, may be null
            public string AutoPick;   // typed file path to return immediately
        }

        private readonly PickMode _mode;
        private readonly HashSet<string> _extensions; // lowercase ".mp3"; null = all
        private readonly bool _allowNewFolder;
        private readonly string _initial;

        private RoundedButton _btnUp;
        private TextBox _txtPath;
        private ListView _lv;
        private ColumnHeader _colName;
        private ColumnHeader _colSize;
        private ColumnHeader _colType;
        private Label _lblEmpty;
        private Label _lblNote;
        private Label _lblSelection;
        private RoundedButton _btnNewFolder;
        private RoundedButton _btnOk;
        private RoundedButton _btnCancel;

        private string _current;      // null = "This PC" drive view
        private string _selFile;      // files mode: picked file path
        private CancellationTokenSource _cts;
        private int _generation;
        private bool _loading;
        private bool _picked;
        private string _result;

        private const int DialogW = 620;
        private const int DialogH = 480;

        private DarkBrowseDialog(PickMode mode, string title, string initialPath, string[] extensions, bool allowNewFolder)
        {
            _mode = mode;
            _extensions = extensions == null ? null : new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            _allowNewFolder = mode == PickMode.Folders && allowNewFolder;
            _initial = initialPath;

            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Card;
            DoubleBuffered = true;
            KeyPreview = true;

            Controls.Add(new DarkTitleBar(this));
            int titleH = DarkTitleBar.BarHeight;

            _btnUp = new RoundedButton();
            _btnUp.Glyph = "\uE74A";
            _btnUp.ButtonStyle = RoundedButton.StyleNeutral;
            _btnUp.AccessibleName = "Up one level";
            _btnUp.SetBounds(16, titleH + 12, 34, 30);
            _btnUp.Click += delegate { GoUp(); };
            Controls.Add(_btnUp);

            _txtPath = new TextBox();
            Theme.StyleInput(_txtPath);
            _txtPath.Font = Theme.Regular;
            _txtPath.PlaceholderText = "This PC - pick a drive or type a path";
            _txtPath.SetBounds(58, titleH + 12, DialogW - 58 - 16, 30);
            Controls.Add(_txtPath);

            _colName = new ColumnHeader(); _colName.Text = "Name"; _colName.Width = 380;
            _colSize = new ColumnHeader(); _colSize.Text = "Size"; _colSize.Width = 90;
            _colType = new ColumnHeader(); _colType.Text = "Type"; _colType.Width = 110;
            _lv = new ListView();
            _lv.View = View.Details;
            _lv.FullRowSelect = true;
            _lv.MultiSelect = false;
            _lv.HideSelection = false;
            _lv.BorderStyle = BorderStyle.None;
            _lv.BackColor = Theme.Input;
            _lv.ForeColor = Theme.Light;
            _lv.Font = Theme.Regular;
            _lv.Columns.Add(_colName);
            if (_mode == PickMode.Files) _lv.Columns.Add(_colSize);
            _lv.Columns.Add(_colType);
            _lv.ItemSelectionChanged += delegate { OnListSelectionChanged(); };
            _lv.ItemActivate += delegate
            {
                ListViewItem it = _lv.SelectedItems.Count > 0 ? _lv.SelectedItems[0] : _lv.FocusedItem;
                ActivateItem(it);
            };
            _lv.SetBounds(16, titleH + 50, DialogW - 32, DialogH - titleH - 50 - 92);
            // Same owner-drawn dark header as the main window's results list
            // (DarkMode_Explorer does not theme the header reliably).
            _lv.OwnerDraw = true;
            _lv.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(Theme.Input))
                    e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.Small,
                    new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 10), e.Bounds.Height),
                    Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                using (Pen p = new Pen(Theme.Border))
                {
                    e.Graphics.DrawLine(p, e.Bounds.Right - 1, 2, e.Bounds.Right - 1, e.Bounds.Height - 2);
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                }
            };
            _lv.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { e.DrawDefault = true; };
            _lv.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e) { e.DrawDefault = true; };
            Controls.Add(_lv);
            DarkControls.MakeDark(_lv);

            _lblEmpty = new Label();
            _lblEmpty.Text = "This folder is empty.";
            _lblEmpty.Font = Theme.Regular;
            _lblEmpty.ForeColor = Theme.Dim;
            _lblEmpty.BackColor = Theme.Input;
            _lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
            _lblEmpty.SetBounds(_lv.Left, _lv.Top, _lv.Width, _lv.Height);
            Controls.Add(_lblEmpty);
            _lblEmpty.BringToFront();

            _lblNote = new Label();
            _lblNote.Font = Theme.Small;
            _lblNote.ForeColor = Theme.Orange;
            _lblNote.SetBounds(16, _lv.Bottom + 4, DialogW - 32, 16);
            Controls.Add(_lblNote);

            _lblSelection = new Label();
            _lblSelection.Font = Theme.Small;
            _lblSelection.ForeColor = Theme.Dim;
            _lblSelection.AutoEllipsis = true;
            _lblSelection.SetBounds(16, _lblNote.Bottom + 2, DialogW - 32, 18);
            Controls.Add(_lblSelection);

            int rowY = DialogH - 14 - 34;
            if (_allowNewFolder)
            {
                _btnNewFolder = new RoundedButton();
                _btnNewFolder.Text = "New folder";
                _btnNewFolder.Glyph = "\uE8B7";
                _btnNewFolder.ButtonStyle = RoundedButton.StyleNeutral;
                _btnNewFolder.Font = Theme.Regular;
                _btnNewFolder.SetBounds(16, rowY, 130, 34);
                _btnNewFolder.Click += delegate { OnNewFolderClick(); };
                Controls.Add(_btnNewFolder);
            }

            _btnCancel = new RoundedButton();
            _btnCancel.Text = "Cancel";
            _btnCancel.ButtonStyle = RoundedButton.StyleNeutral;
            _btnCancel.Font = Theme.Regular;
            _btnCancel.SetBounds(DialogW - 16 - 90, rowY, 90, 34);
            _btnCancel.Click += delegate { Close(); };
            Controls.Add(_btnCancel);

            _btnOk = new RoundedButton();
            _btnOk.Text = _mode == PickMode.Folders ? "Select folder" : "Open";
            _btnOk.ButtonStyle = RoundedButton.StyleGreen;
            _btnOk.Font = Theme.RegularBold;
            _btnOk.SetBounds(DialogW - 16 - 90 - 10 - 110, rowY, 110, 34);
            _btnOk.Click += delegate { ConfirmOk(); };
            Controls.Add(_btnOk);

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
                else if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    if (_txtPath.Focused) NavigateFromTextBox();
                    else if (_btnOk.Enabled) ConfirmOk();
                }
            };

            ClientSize = new Size(DialogW, DialogH);
            StretchNameColumn();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Same chrome as the main window; the non-client frame is removed,
            // so the custom dark strip must paint above the removed frame area.
            ChromeApi.ApplyChromeStyles(Handle);
            ChromeApi.ApplyRoundedCorners(Handle);
            // Initial navigation happens here, inside the modal loop: a stale
            // or renamed initial folder simply falls back to the drive view.
            Navigate(_initial);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            CancellationTokenSource c = _cts;
            _cts = null;
            if (c != null) { try { c.Cancel(); } catch { } c.Dispose(); }
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

        // ---------- navigation ----------

        private void Navigate(string path)
        {
            _current = path;
            _txtPath.Text = path ?? "";
            _txtPath.SelectionStart = _txtPath.TextLength;
            StartLoad();
        }

        private void GoUp()
        {
            if (_current == null) return;
            try
            {
                DirectoryInfo parent = new DirectoryInfo(_current).Parent;
                Navigate(parent == null ? null : parent.FullName);
            }
            catch { Navigate(null); }
        }

        // The path box is live: Enter navigates to the typed path (a folder,
        // or in files mode a file is picked directly). Empty = This PC.
        private void NavigateFromTextBox()
        {
            string raw = _txtPath.Text.Trim().Trim('"');
            if (raw.Length == 0) { Navigate(null); return; }
            // "E:" alone resolves to the current directory of drive E; make it
            // the drive root, which is what the user means here.
            if (raw.Length == 2 && raw[1] == ':') raw += "\\";
            string full;
            try { full = Path.GetFullPath(raw); }
            catch { ShowNote("Not a valid path: " + raw); return; }
            Navigate(full);
        }

        // ---------- listing (worker thread; UI never blocks) ----------

        private void StartLoad()
        {
            int gen = ++_generation;
            CancellationTokenSource old = _cts;
            _cts = new CancellationTokenSource();
            if (old != null) { try { old.Cancel(); } catch { } old.Dispose(); }
            CancellationToken ct = _cts.Token;
            string target = _current;

            _loading = true;
            _selFile = null;
            _lv.Items.Clear();
            _lblNote.Text = "";
            ShowOverlay("Loading...");
            UpdateOkEnabled();
            UpdateSelectionLabel();

            Task<Listing> task = Task.Run(delegate { return BuildListing(gen, ct, target); });
            task.ContinueWith(delegate(Task<Listing> t)
            {
                if (IsDisposed || Disposing) return;
                _loading = false;
                if (t.IsCanceled || t.IsFaulted) return;   // stale or canceled
                Listing l = t.Result;
                if (l == null || l.Generation != _generation) return; // stale
                if (l.DrivesView && _current != null)
                {
                    _current = null;
                    _txtPath.Text = "";
                }
                if (!string.IsNullOrEmpty(l.AutoPick))
                {
                    _result = l.AutoPick;
                    _picked = true;
                    Close();
                    return;
                }
                Fill(l);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private Listing BuildListing(int gen, CancellationToken ct, string target)
        {
            Listing l = new Listing();
            l.Generation = gen;
            l.Items = new List<Entry>();
            try
            {
                if (target != null)
                {
                    if (!Directory.Exists(target))
                    {
                        if (File.Exists(target) && _mode == PickMode.Files)
                        {
                            l.AutoPick = target;
                            return l;
                        }
                        if (File.Exists(target))
                        {
                            // A file path typed in folders mode: browse its folder.
                            string parent = Path.GetDirectoryName(target);
                            target = string.IsNullOrEmpty(parent) ? null : parent;
                        }
                        else
                        {
                            l.Error = "Path not found: " + target;
                            target = null; // fall back to the drive view
                        }
                    }
                }
                if (target == null)
                {
                    l.DrivesView = true;
                    foreach (DriveInfo d in DriveInfo.GetDrives())
                    {
                        ct.ThrowIfCancellationRequested();
                        Entry en = new Entry();
                        en.IsFolder = d.IsReady;
                        en.TypeText = d.IsReady ? DriveTypeName(d.DriveType) : "Not ready";
                        en.FullPath = d.IsReady ? d.Name : null;
                        string label = null;
                        if (d.IsReady) { try { label = d.VolumeLabel; } catch { } }
                        en.Display = string.IsNullOrEmpty(label) ? d.Name : label + " (" + d.Name + ")";
                        l.Items.Add(en);
                    }
                    return l;
                }

                EnumerationOptions opt = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    ReturnSpecialDirectories = false
                };
                foreach (string dir in Directory.EnumerateDirectories(target, "*", opt))
                {
                    ct.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(dir);
                    if (name.Length == 0) continue;
                    l.Items.Add(new Entry { Display = name, FullPath = dir, IsFolder = true, TypeText = "File folder" });
                }
                if (_mode == PickMode.Files)
                {
                    foreach (string f in Directory.EnumerateFiles(target, "*", opt))
                    {
                        ct.ThrowIfCancellationRequested();
                        string ext = Path.GetExtension(f);
                        if (_extensions != null && !_extensions.Contains(ext)) continue;
                        Entry en = new Entry
                        {
                            Display = Path.GetFileName(f),
                            FullPath = f,
                            TypeText = ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() + " file" : "File"
                        };
                        try { en.SizeBytes = new FileInfo(f).Length; } catch { }
                        l.Items.Add(en);
                    }
                }
            }
            catch (OperationCanceledException) { throw; } // stale: dropped by caller
            catch (UnauthorizedAccessException uax) { l.Error = "Access denied: " + uax.Message; }
            catch (IOException iox) { l.Error = iox.Message; }
            catch (Exception ex) { l.Error = ex.Message; }

            // Folders first, then files, each alphabetically (case-insensitive).
            l.Items.Sort(delegate(Entry a, Entry b)
            {
                int fa = a.IsFolder ? 0 : 1, fb = b.IsFolder ? 0 : 1;
                if (fa != fb) return fa - fb;
                return string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
            });
            return l;
        }

        private void Fill(Listing l)
        {
            _lv.BeginUpdate();
            _lv.Items.Clear();
            foreach (Entry en in l.Items)
            {
                ListViewItem it = new ListViewItem(en.Display ?? "");
                it.Tag = en;
                if (en.SizeBytes.HasValue) it.SubItems.Add(UiFormat.Size(en.SizeBytes.Value));
                it.SubItems.Add(en.TypeText ?? "");
                if (en.FullPath == null) it.ForeColor = Theme.Faint;
                _lv.Items.Add(it);
            }
            _lv.EndUpdate();
            _lblNote.Text = l.Error ?? "";
            bool empty = _lv.Items.Count == 0;
            ShowOverlay(empty ? (l.Error != null ? "Folder is not accessible." : "This folder is empty.") : null);
            StretchNameColumn();
            UpdateOkEnabled();
            UpdateSelectionLabel();
        }

        // ---------- actions ----------

        private void ActivateItem(ListViewItem it)
        {
            Entry en = it == null ? null : it.Tag as Entry;
            if (en == null) return;
            if (en.FullPath == null) { ShowNote(en.Display + " is not ready."); return; }
            if (en.IsFolder) { Navigate(en.FullPath); return; }
            if (_mode == PickMode.Files) { _result = en.FullPath; _picked = true; Close(); }
        }

        private void ConfirmOk()
        {
            if (_loading) return;
            if (_mode == PickMode.Folders)
            {
                if (_current == null) return;
                _result = _current;
            }
            else
            {
                if (_selFile == null) return;
                _result = _selFile;
            }
            _picked = true;
            Close();
        }

        // Creates a uniquely named subfolder of the current directory on the
        // worker thread (a slow network path must not block the UI), then
        // navigates into it on success.
        private void OnNewFolderClick()
        {
            if (_loading) return;
            if (_current == null) { ShowNote("Pick a drive first."); return; }
            string name = PromptDialog.Show(this, "New folder", "Folder name:", "New folder");
            if (name == null) return;
            string baseDir = _current;
            string target = UniqueDirectory(baseDir, name);
            _loading = true;
            ShowOverlay("Creating...");
            Task.Run(delegate
            {
                try { Directory.CreateDirectory(target); return target; }
                catch (Exception ex) { return "ERR:" + ex.Message; }
            })
            .ContinueWith(delegate(Task<string> t)
            {
                if (IsDisposed || Disposing) { _loading = false; return; }
                _loading = false;
                if (t.Result != null && t.Result.StartsWith("ERR:", StringComparison.Ordinal))
                {
                    ShowNote("Could not create the folder: " + t.Result.Substring(4));
                    return;
                }
                Navigate(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static string UniqueDirectory(string baseDir, string name)
        {
            string candidate = Path.Combine(baseDir, name);
            int i = 2;
            while (Directory.Exists(candidate) || File.Exists(candidate))
            {
                candidate = Path.Combine(baseDir, name + " (" + i + ")");
                i++;
            }
            return candidate;
        }

        // ---------- state UI ----------

        private void OnListSelectionChanged()
        {
            _selFile = null;
            if (_lv.SelectedItems.Count > 0)
            {
                Entry en = _lv.SelectedItems[0].Tag as Entry;
                if (en != null && !en.IsFolder && en.FullPath != null) _selFile = en.FullPath;
            }
            UpdateOkEnabled();
            UpdateSelectionLabel();
        }

        private void UpdateOkEnabled()
        {
            _btnOk.Enabled = _loading ? false
                : _mode == PickMode.Folders ? _current != null
                : _selFile != null;
        }

        private void UpdateSelectionLabel()
        {
            if (_mode == PickMode.Folders)
                _lblSelection.Text = _current == null
                    ? "Pick a drive below, then browse to a folder."
                    : "Folder: " + _current;
            else
                _lblSelection.Text = _selFile == null ? "Choose a file in the list." : "File: " + _selFile;
        }

        private void ShowOverlay(string text)
        {
            if (text == null) { _lblEmpty.Visible = false; return; }
            _lblEmpty.Text = text;
            _lblEmpty.Visible = true;
            _lblEmpty.BringToFront();
        }

        private void ShowNote(string text)
        {
            _lblNote.Text = text;
        }

        private void StretchNameColumn()
        {
            if (_lv.Columns.Count < 2) return;
            int fixedW = 0;
            foreach (ColumnHeader c in _lv.Columns)
                if (c != _colName) fixedW += c.Width;
            int rest = _lv.ClientSize.Width - fixedW - 4;
            if (rest > 120) _colName.Width = rest;
        }

        private static string DriveTypeName(DriveType t)
        {
            switch (t)
            {
                case DriveType.Fixed: return "Local Disk";
                case DriveType.Network: return "Network Drive";
                case DriveType.Removable: return "Removable Drive";
                case DriveType.CDRom: return "Optical Drive";
                case DriveType.Ram: return "RAM Disk";
                default: return "Drive";
            }
        }

        // ---------- modal entry points ----------

        // Returns the picked folder, or null when cancelled. allowNewFolder
        // keeps the old FolderBrowserDialog's ShowNewFolderButton ability.
        public static string ShowFolderPicker(Form owner, string title, string initialFolder, bool allowNewFolder)
        {
            using (DarkBrowseDialog f = new DarkBrowseDialog(PickMode.Folders, title, initialFolder, null, allowNewFolder))
            {
                RunModal(f, owner);
                return f._picked ? f._result : null;
            }
        }

        // Returns the picked file, or null when cancelled. extensions are
        // lowercase extensions with dot (".mp3"); null shows all files.
        public static string ShowFilePicker(Form owner, string title, string initialDir, string[] extensions)
        {
            using (DarkBrowseDialog f = new DarkBrowseDialog(PickMode.Files, title, initialDir, extensions, false))
            {
                RunModal(f, owner);
                return f._picked ? f._result : null;
            }
        }

        private static void RunModal(DarkBrowseDialog f, Form owner)
        {
            if (owner == null || owner.IsDisposed)
            {
                if (Application.OpenForms.Count > 0) owner = Application.OpenForms[0];
            }
            if (owner != null && !owner.IsDisposed) f.ShowDialog(owner);
            else f.ShowDialog();
        }
    }

    // Tiny modal input prompt (used for "New folder" naming), same dark chrome.
    internal sealed class PromptDialog : Form
    {
        private readonly TextBox _txt;
        private bool _ok;

        private PromptDialog(string title, string message, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Card;
            DoubleBuffered = true;
            KeyPreview = true;

            Controls.Add(new DarkTitleBar(this));
            int titleH = DarkTitleBar.BarHeight;

            const int w = 380;
            Label msg = new Label();
            msg.Text = message;
            msg.Font = Theme.Regular;
            msg.ForeColor = Theme.Light;
            msg.SetBounds(16, titleH + 12, w - 32, 20);
            Controls.Add(msg);

            _txt = new TextBox();
            Theme.StyleInput(_txt);
            _txt.Font = Theme.Regular;
            _txt.Text = initial;
            _txt.SetBounds(16, titleH + 40, w - 32, 28);
            Controls.Add(_txt);

            int rowY = titleH + 84;
            RoundedButton btnCancel = new RoundedButton();
            btnCancel.Text = "Cancel";
            btnCancel.ButtonStyle = RoundedButton.StyleNeutral;
            btnCancel.Font = Theme.Regular;
            btnCancel.SetBounds(w - 16 - 90, rowY, 90, 32);
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);

            RoundedButton btnOk = new RoundedButton();
            btnOk.Text = "OK";
            btnOk.ButtonStyle = RoundedButton.StyleGreen;
            btnOk.Font = Theme.RegularBold;
            btnOk.SetBounds(w - 16 - 90 - 10 - 90, rowY, 90, 32);
            btnOk.Click += delegate { _ok = true; Close(); };
            Controls.Add(btnOk);

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
                else if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    if (_txt.Text.Trim().Length > 0) { _ok = true; Close(); }
                }
            };

            ClientSize = new Size(w, rowY + 32 + 14);
            ActiveControl = _txt;
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

        public static string Show(Form owner, string title, string message, string initial)
        {
            using (PromptDialog f = new PromptDialog(title, message, initial))
            {
                if (owner != null && !owner.IsDisposed) f.ShowDialog(owner);
                else f.ShowDialog();
                return f._ok && f._txt.Text.Trim().Length > 0 ? f._txt.Text.Trim() : null;
            }
        }
    }
}
