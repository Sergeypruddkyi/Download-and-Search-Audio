using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace DownloadSearchAudio
{
    // Atomic INI persistence next to the EXE, adapted from YouTubeDownloader-Net10
    // src\Settings.cs (write .tmp, File.Replace with .bak, IOException fallback).
    // Keys adapted for this app.
    public class Settings
    {
        private readonly string _path;
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Settings(string path)
        {
            _path = path;
        }

        // Download Audio: last save folder (shared with search folder default).
        public string LastFolder
        {
            get { return Get("LastFolder"); }
            set { Set("LastFolder", value); }
        }

        public bool HasChosenFolder
        {
            get { return string.Equals(Get("FolderChosen"), "true", StringComparison.OrdinalIgnoreCase); }
            set { Set("FolderChosen", value ? "true" : null); }
        }

        // Music Search: last search root and subfolder toggle.
        public string SearchFolder
        {
            get { return Get("SearchFolder"); }
            set { Set("SearchFolder", value); }
        }

        public bool IncludeSubfolders
        {
            get { return !string.Equals(Get("IncludeSubfolders"), "false", StringComparison.OrdinalIgnoreCase); }
            set { Set("IncludeSubfolders", value ? null : "false"); }
        }

        // Non-secret cookie-jar metadata: the yt-dlp browser key (e.g. "chrome")
        // the saved jar was exported from, recorded only AFTER a successful
        // export so the cookies dialog can show the source. No cookie data here.
        public string CookiesSourceBrowser
        {
            get { return Get("CookiesSourceBrowser"); }
            set { Set("CookiesSourceBrowser", value); }
        }

        // Download Audio: output container preference ("mp3" | "original").
        // This is NOT a quality/bitrate setting: the audio stream is always
        // Best available.
        public string AudioFormat
        {
            get { return Get("AudioFormat"); }
            set { Set("AudioFormat", value); }
        }

        public string Get(string key)
        {
            string v;
            return _values.TryGetValue(key, out v) ? v : null;
        }

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) _values.Remove(key);
            else _values[key] = value;
        }

        private static bool _loadFailedWarned;
        private static bool _saveFailedWarned;

        private static void WarnOnce(ref bool warned, string action, Exception ex)
        {
            if (warned) return;
            warned = true;
            try
            {
                MessageBox.Show("Failed to " + action + " settings:\n" + ex.Message,
                    "Download and Search Audio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
                _values.Clear();
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    _values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                WarnOnce(ref _loadFailedWarned, "load", ex);
            }
        }

        public void Save()
        {
            string tmp = _path + ".tmp";
            string bak = _path + ".bak";
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("[General]");
                foreach (KeyValuePair<string, string> kv in _values)
                    sb.AppendLine(kv.Key + "=" + kv.Value);

                string content = sb.ToString();
                try
                {
                    File.WriteAllText(tmp, content, Encoding.UTF8);
                    if (File.Exists(_path))
                    {
                        try
                        {
                            File.Replace(tmp, _path, bak, true);
                        }
                        catch (IOException)
                        {
                            try { if (File.Exists(bak)) File.Delete(bak); }
                            catch { }
                            File.WriteAllText(_path, content, Encoding.UTF8);
                        }
                    }
                    else
                    {
                        File.Move(tmp, _path);
                    }
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); }
                    catch { }
                    try { if (File.Exists(bak)) File.Delete(bak); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                WarnOnce(ref _saveFailedWarned, "save", ex);
            }
        }
    }
}
