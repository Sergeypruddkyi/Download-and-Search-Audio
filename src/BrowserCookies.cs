using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DownloadSearchAudio
{
    public sealed class BrowserInfo
    {
        public string Key { get; set; }
        public string Display { get; set; }
        public bool Installed { get; set; }
        public bool IsDefault { get; set; }
    }

    // Determines the Windows default browser through the standard http/https URL
    // association (AssocQueryString, with the UserChoice ProgId as a fallback) and
    // maps it to a yt-dlp browser key. It also reports which of the
    // yt-dlp-supported browsers look installed, by the mere presence of their
    // profile/data directory.
    //
    // Nothing here reads cookies, history or any other personal data: detection
    // only inspects the URL association and whether a directory exists. The set
    // of browser keys is the one the bundled yt.exe advertises for
    // --cookies-from-browser (brave, chrome, chromium, edge, firefox, opera,
    // safari, vivaldi, whale); Safari is macOS-only, so it is not offered here.
    // Adapted from YouTubeDownloader-Net10 src\BrowserCookies.cs (namespace only;
    // the read-only browser-detection design was already exactly what this app
    // needed, so no behaviour was changed).
    public static class BrowserCookies
    {
        private const int ASSOCF_NONE = 0;
        private const int ASSOCSTR_EXECUTABLE = 2;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int AssocQueryString(int flags, int str, string pszAssoc, string pszExtra,
            StringBuilder pszOut, ref uint pcchOut);

        private static readonly string[] Keys =
            { "firefox", "chrome", "edge", "brave", "chromium", "vivaldi", "opera" };

        private static readonly Dictionary<string, string> Display =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "firefox", "Mozilla Firefox" },
            { "chrome", "Google Chrome" },
            { "edge", "Microsoft Edge" },
            { "brave", "Brave" },
            { "chromium", "Chromium" },
            { "vivaldi", "Vivaldi" },
            { "opera", "Opera" }
        };

        public static IReadOnlyList<string> SupportedKeys { get { return Keys; } }

        public static string DisplayForKey(string key)
        {
            string d;
            return key != null && Display.TryGetValue(key, out d) ? d : (key ?? "");
        }

        public static bool IsSupported(string key)
        {
            return key != null && Array.IndexOf(Keys, key.ToLowerInvariant()) >= 0;
        }

        // Maps a browser executable file name to a yt-dlp browser key.
        public static string KeyFromExePath(string exePath)
        {
            string name;
            try { name = Path.GetFileName(exePath ?? "").ToLowerInvariant(); }
            catch { return null; }
            switch (name)
            {
                case "firefox.exe": return "firefox";
                case "chrome.exe": return "chrome";
                case "msedge.exe": return "edge";
                case "brave.exe": return "brave";
                case "chromium.exe": return "chromium";
                case "vivaldi.exe": return "vivaldi";
                case "opera.exe": return "opera";
                case "launcher.exe": return "opera"; // Opera / Opera GX launcher
                default: return null;
            }
        }

        // Fallback mapping from the registered http/https handler ProgId.
        public static string KeyFromProgId(string progId)
        {
            if (string.IsNullOrEmpty(progId)) return null;
            string p = progId.ToLowerInvariant();
            if (p.Contains("firefox")) return "firefox";
            if (p.Contains("chromium")) return "chromium";
            if (p.Contains("chrome")) return "chrome";
            if (p.Contains("brave")) return "brave";
            if (p.Contains("vivaldi")) return "vivaldi";
            if (p.Contains("opera")) return "opera";
            if (p.Contains("edge")) return "edge"; // MSEdgeHTM / MicrosoftEdge
            return null;
        }

        // yt-dlp key of the Windows default browser, or null when it cannot be
        // determined or is not supported.
        public static string DetectDefaultKey()
        {
            string key = KeyFromExePath(DefaultBrowserExe());
            if (key != null) return key;
            return KeyFromProgId(DefaultBrowserProgId());
        }

        // Full path of the default http handler ("standard mechanism").
        public static string DefaultBrowserExe()
        {
            try
            {
                uint cap = 1024;
                for (int i = 0; i < 6; i++)
                {
                    StringBuilder sb = new StringBuilder((int)cap);
                    uint len = cap;
                    int hr = AssocQueryString(ASSOCF_NONE, ASSOCSTR_EXECUTABLE, "http", null, sb, ref len);
                    if (hr == 0) return sb.Length > 0 ? sb.ToString() : null;
                    if (len > cap) { cap = len + 1; continue; }
                    return null;
                }
            }
            catch { }
            return null;
        }

        public static string DefaultBrowserProgId()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"))
                {
                    if (k != null) return k.GetValue("ProgId") as string;
                }
            }
            catch { }
            return null;
        }

        // Supported browsers whose profile/data directory exists, with the
        // default one marked. Used to offer an alternative source when the
        // default browser is missing, unsupported or has no usable cookies.
        public static List<BrowserInfo> InstalledSupported()
        {
            string def = DetectDefaultKey();
            List<BrowserInfo> list = new List<BrowserInfo>();
            foreach (string key in Keys)
            {
                if (!DataDirExists(key)) continue;
                list.Add(new BrowserInfo
                {
                    Key = key,
                    Display = DisplayForKey(key),
                    Installed = true,
                    IsDefault = string.Equals(key, def, StringComparison.OrdinalIgnoreCase)
                });
            }
            return list;
        }

        private static bool DataDirExists(string key)
        {
            foreach (string dir in DataDirs(key))
            {
                try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return true; }
                catch { }
            }
            return false;
        }

        private static IEnumerable<string> DataDirs(string key)
        {
            string roaming = null, local = null;
            try { roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } catch { }
            try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } catch { }
            switch (key)
            {
                case "firefox":
                    if (roaming != null) yield return Path.Combine(roaming, "Mozilla", "Firefox");
                    if (local != null) yield return Path.Combine(local, "Mozilla", "Firefox");
                    break;
                case "chrome":
                    if (local != null) yield return Path.Combine(local, "Google", "Chrome", "User Data");
                    break;
                case "edge":
                    if (local != null) yield return Path.Combine(local, "Microsoft", "Edge", "User Data");
                    break;
                case "brave":
                    if (local != null) yield return Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data");
                    break;
                case "chromium":
                    if (local != null) yield return Path.Combine(local, "Chromium", "User Data");
                    break;
                case "vivaldi":
                    if (local != null) yield return Path.Combine(local, "Vivaldi", "User Data");
                    break;
                case "opera":
                    if (roaming != null) yield return Path.Combine(roaming, "Opera Software", "Opera Stable");
                    if (roaming != null) yield return Path.Combine(roaming, "Opera Software", "Opera GX Stable");
                    break;
            }
        }
    }
}
