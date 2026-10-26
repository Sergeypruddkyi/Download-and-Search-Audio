using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DownloadSearchAudio
{
    public sealed class CookiePrepareResult
    {
        public bool Success;
        public string FilePath;
        // yt-dlp browser key the cookies were taken from (e.g. "chrome").
        public string Browser;
        // True when the produced jar contains at least one YouTube cookie whose
        // NAME is a known sign-in cookie. Names only - values are never read or
        // exposed. A false value does not prove the user is signed out, so the
        // jar is still kept; it only tells the caller the session may be missing.
        public bool LooksSignedIn;
        // Sanitized, user-showable reason. Never contains cookie values.
        public string Error;
    }

    // Outcome of a request to remove the saved cookie jar. NothingToRemove is
    // deliberately distinct from Removed so the caller can tell the user "there
    // was nothing saved" instead of reporting a successful deletion.
    public enum CookieClearStatus
    {
        Removed,
        NothingToRemove,
        // The requested path resolved outside the allowed store: nothing was
        // touched. Defensive - the canonical path can never hit this.
        Refused,
        Failed
    }

    public sealed class CookieClearResult
    {
        public CookieClearStatus Status;
        // Sanitized, user-showable reason. Never contains cookie values.
        public string Error;
    }

    // A short-lived private copy used when yt-dlp needs a cookie jar. yt-dlp
    // REWRITES whatever file is passed to --cookies, so callers must never pass
    // the user's saved jar directly. Adapted from YouTubeDownloader-Net10
    // src\CookieStore.cs (StagedCookieFile): disposal removes the staging
    // directory; nothing outside it is ever touched.
    public sealed class StagedCookieFile : IDisposable
    {
        private readonly string _directory;
        public string FilePath { get; private set; }

        internal StagedCookieFile(string directory, string filePath)
        {
            _directory = directory;
            FilePath = filePath;
        }

        public void Dispose()
        {
            string path = _directory;
            FilePath = null;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch { }
        }
    }

    // User-scoped store for the saved YouTube cookies. Adapted from
    // YouTubeDownloader-Net10 src\CookieStore.cs; the dropped parts of the
    // source (the exit-time temporary-cookie registry) are not needed here
    // because staged copies live inside the per-session work directory that
    // AppPaths.CleanupSessionWorkDir already removes at close.
    //
    // Location: <application folder>\data\cookies\cookies.txt. It is not part
    // of the release payload and remains until the user explicitly deletes it.
    // The file is never printed and never written to a log; only cookie NAMES
    // (never values) are inspected locally to judge whether the jar is usable
    // and export verification reads names only.
    public static class CookieStore
    {
        public static string FilePath { get { return AppPaths.CookieFile; } }

        // Suffix of the half-written export that must never be mistaken for the
        // finished jar. Shared by the export and the clear operation.
        private const string TempSuffix = ".tmp";

        public static bool HasUsableCookies()
        {
            try
            {
                string path = AppPaths.CookieFile;
                return File.Exists(path) && new FileInfo(path).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        // Returns the stored jar only when it exists and is non-empty; null
        // otherwise, so the caller can distinguish "retried with cookies" from
        // "the reserve attempt was unavailable".
        public static string UsableFile()
        {
            return HasUsableCookies() ? AppPaths.CookieFile : null;
        }

        // yt-dlp may update a cookie jar after a request. Give every request a
        // private copy in a unique temporary directory, never the saved store.
        public static StagedCookieFile StageUsableCopy()
        {
            return StageCopyForRequest(UsableFile());
        }

        public static StagedCookieFile StageCopyForRequest(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            string directory = null;
            try
            {
                directory = AppPaths.CreateWorkDirectory("cookies");
                string staged = Path.Combine(directory, "cookies.txt");
                File.Copy(source, staged, false);
                return new StagedCookieFile(directory, staged);
            }
            catch
            {
                try { if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) Directory.Delete(directory, true); }
                catch { }
                return null;
            }
        }

        // Removes the saved cookie jar and its `.tmp` sibling - and nothing else.
        // This is the only destructive operation on the store: it never deletes
        // the store directory, browser data, settings or any other file.
        public static CookieClearResult ClearSaved()
        {
            return ClearFiles(AppPaths.CookieFile, AppPaths.CookieDir);
        }

        // Testable core of ClearSaved. Deletes `file` and `<file>.tmp` only when
        // `file` resolves inside `allowedRoot`; every other path is refused
        // without touching the disk. The root comparison is segment-aware, so a
        // sibling directory whose name merely starts with the root's name is not
        // treated as inside the store.
        public static CookieClearResult ClearFiles(string file, string allowedRoot)
        {
            if (string.IsNullOrEmpty(file))
                return new CookieClearResult { Status = CookieClearStatus.Failed, Error = "no cookie file is configured" };
            try
            {
                string fullFile = Path.GetFullPath(file);
                if (!IsInsideRoot(fullFile, allowedRoot))
                    return new CookieClearResult { Status = CookieClearStatus.Refused,
                        Error = "refused: the path is outside the application cookie store" };

                string temp = fullFile + TempSuffix;
                bool removedJar = false;
                if (File.Exists(fullFile))
                {
                    try { File.Delete(fullFile); removedJar = true; }
                    catch (Exception ex) when (AppPaths.IsMissingPath(ex) && !File.Exists(fullFile))
                    {
                        // Removed concurrently; the end state is identical.
                    }
                }
                bool removedTemp = false;
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); removedTemp = true; }
                    catch (Exception ex) when (AppPaths.IsMissingPath(ex) && !File.Exists(temp))
                    {
                        // Removed concurrently; the end state is identical.
                    }
                }

                return new CookieClearResult
                {
                    Status = (removedJar || removedTemp) ? CookieClearStatus.Removed : CookieClearStatus.NothingToRemove
                };
            }
            catch (Exception ex)
            {
                return new CookieClearResult { Status = CookieClearStatus.Failed, Error = DiagnosticSanitizer.Sanitize(ex.Message) };
            }
        }

        private static bool IsInsideRoot(string fullPath, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string fullRoot;
            try { fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return false; }
            if (fullRoot.Length == 0) return false;
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // Exports cookies from the given yt-dlp browser (firefox/chrome/edge/...)
        // into the store. The export is written to a sibling temporary file first
        // and only replaces the store after it was verified to contain at least
        // one YouTube cookie - so a failed or empty export never destroys a
        // previously working copy. The temporary file is always removed on
        // failure. onStarted receives the yt.exe process so the caller can track
        // it (cancellation, window close).
        public static async Task<CookiePrepareResult> PrepareFromBrowserAsync(string browser,
            Action<System.Diagnostics.Process> onStarted)
        {
            string key = BrowserCookies.IsSupported(browser)
                ? browser.ToLowerInvariant()
                : (browser ?? "");
            string target = AppPaths.CookieFile;
            string temp = target + TempSuffix;
            try
            {
                Directory.CreateDirectory(AppPaths.CookieDir);
            }
            catch (Exception ex)
            {
                DiagLog.Event("cookies", "export start failed for " + key + ": " + ex.Message);
                return Fail(key, DiagnosticSanitizer.Sanitize(ex.Message));
            }
            TryDelete(temp);

            YtRunResult run;
            try
            {
                DiagLog.Event("cookies", "export start: browser=" + key + " (no URL sent: offline export)");
                run = await YtDlpRunner.RunAsync(YtDlpRunner.SaveCookiesArgs(temp, key), null, onStarted)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                DiagLog.Event("cookies", "export failed for " + key + ": " + ex.Message);
                return Fail(key, DiagnosticSanitizer.Sanitize(ex.Message));
            }

            bool hasYouTube;
            bool signedIn;
            string reason;
            if (!AnalyzeJar(temp, out hasYouTube, out signedIn, out reason))
            {
                TryDelete(temp);
                // yt-dlp writes the jar even when it is empty; a genuine failure
                // (browser profile locked / not found / unsupported) shows up as
                // an ERROR line.
                string hint = LastErrorLine(run != null ? run.Output : null);
                DiagLog.Event("cookies", "export produced no usable jar for " + key
                    + (hint != null ? "; output: " + hint : ""));
                return Fail(key, hint != null ? reason + " (" + hint + ")" : reason);
            }

            try
            {
                if (File.Exists(target)) File.Replace(temp, target, null);
                else File.Move(temp, target);
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                DiagLog.Event("cookies", "export replace failed for " + key + ": " + ex.Message);
                return Fail(key, DiagnosticSanitizer.Sanitize(ex.Message));
            }
            // Only non-secret metadata is logged: browser key, file path and
            // whether a sign-in cookie NAME was present. Never any value.
            DiagLog.Event("cookies", "export ok: browser=" + key + " jar=" + target + " signedIn=" + signedIn);
            return new CookiePrepareResult { Success = true, FilePath = target, Browser = key, LooksSignedIn = signedIn };
        }

        private static CookiePrepareResult Fail(string browser, string reason)
        {
            return new CookiePrepareResult { Success = false, FilePath = AppPaths.CookieFile, Browser = browser, Error = reason };
        }

        // Verification reads cookie NAMES only (never values) and requires a
        // non-empty Netscape jar with at least one YouTube-scoped line. Presence
        // of a known sign-in cookie is reported separately; nothing is logged.
        public static bool AnalyzeJar(string path, out bool hasYouTube, out bool signedIn, out string reason)
        {
            hasYouTube = false;
            signedIn = false;
            reason = null;
            try
            {
                if (!File.Exists(path)) { reason = "cookies were not produced"; return false; }
                if (new FileInfo(path).Length == 0) { reason = "the cookies file is empty"; return false; }
                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] fields = line.Split('\t');
                    if (fields.Length < 6) continue;
                    bool youtube = line.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!youtube) continue;
                    hasYouTube = true;
                    if (IsSignInCookieName(fields[5])) signedIn = true;
                }
                if (!hasYouTube) { reason = "no YouTube cookies were found in the selected browser profile"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                reason = DiagnosticSanitizer.Sanitize(ex.Message);
                return false;
            }
        }

        // Google/YouTube session cookie names. Compared exactly (case-sensitive)
        // against the jar's name field.
        private static readonly HashSet<string> SignInCookieNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "SID", "SSID", "HSID", "SAPISID", "APISID",
            "__Secure-1PSID", "__Secure-3PSID",
            "__Secure-1PAPISID", "__Secure-3PAPISID",
            "LOGIN_INFO", "SIDCC", "__Secure-1PSIDCC", "__Secure-3PSIDCC"
        };

        private static bool IsSignInCookieName(string name)
        {
            return !string.IsNullOrEmpty(name) && SignInCookieNames.Contains(name);
        }

        private static string LastErrorLine(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string s = lines[i].Trim();
                if (s.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) return DiagnosticSanitizer.Sanitize(s);
            }
            return null;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
