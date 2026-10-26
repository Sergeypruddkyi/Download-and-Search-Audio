using System;
using System.IO;

namespace DownloadSearchAudio
{
    // Portable sidecar layout, adapted from YouTubeDownloader-Net10 src\AppPaths.cs:
    // everything lives next to the EXE, nothing in the registry. Tools are checked
    // (not downloaded) — yt.exe/ffmpeg.exe/ffprobe.exe will be copied from the
    // existing YouTubeDownloader dist at Stage 2.
    public static class AppPaths
    {
        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string YtExe { get { return Path.Combine(BaseDir, "yt.exe"); } }
        public static string DenoExe { get { return Path.Combine(BaseDir, "deno.exe"); } }
        public static string FfmpegExe { get { return Path.Combine(BaseDir, "ffmpeg.exe"); } }
        public static string FfprobeExe { get { return Path.Combine(BaseDir, "ffprobe.exe"); } }
        public static string SettingsPath { get { return Path.Combine(BaseDir, "settings.ini"); } }
        public static string DataDir { get { return Path.Combine(BaseDir, "data"); } }
        public static string WorkDir { get { return Path.Combine(DataDir, "work"); } }

        // Saved YouTube cookie jar (adapted from YouTubeDownloader-Net10
        // src\AppPaths.cs): exported by yt-dlp itself on the user's explicit
        // request, stored in the portable data tree, never part of the release
        // payload and never printed or logged.
        public static string CookieDir { get { return Path.Combine(DataDir, "cookies"); } }
        public static string CookieFile { get { return Path.Combine(CookieDir, "cookies.txt"); } }
        public static string LogsDir { get { return Path.Combine(DataDir, "logs"); } }

        // Per-run scratch dir handed to yt-dlp as TEMP/TMP so its PyInstaller
        // unpack stays inside the app's portable data tree (same scheme as the
        // source project). One session per process run.
        private static string _sessionWorkDir;

        public static string SessionWorkDir
        {
            get
            {
                if (_sessionWorkDir == null)
                {
                    _sessionWorkDir = Path.Combine(WorkDir, "session-" + Guid.NewGuid().ToString("N").Substring(0, 12));
                }
                return _sessionWorkDir;
            }
        }

        public static bool YtPresent()
        {
            return File.Exists(YtExe);
        }

        public static bool FfmpegPresent()
        {
            return File.Exists(FfmpegExe);
        }

        public static bool FfprobePresent()
        {
            return File.Exists(FfprobeExe);
        }

        // First missing required tool, or null when all are present next to the EXE.
        public static string MissingCoreComponent()
        {
            if (!File.Exists(YtExe)) return YtExe;
            if (!File.Exists(DenoExe)) return DenoExe;
            if (!File.Exists(FfmpegExe)) return FfmpegExe;
            return null;
        }

        public static void InitializePortableStorage()
        {
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                if (!Directory.Exists(WorkDir)) Directory.CreateDirectory(WorkDir);
                if (!Directory.Exists(CookieDir)) Directory.CreateDirectory(CookieDir);
                if (!Directory.Exists(LogsDir)) Directory.CreateDirectory(LogsDir);
                if (!Directory.Exists(SessionWorkDir)) Directory.CreateDirectory(SessionWorkDir);
            }
            catch { }
        }

        // Unique scratch directory under the session work root, used for staged
        // cookie copies (same scheme as the source project's CreateWorkDirectory).
        // Returns null instead of throwing; callers treat null as "no staged copy".
        public static string CreateWorkDirectory(string purpose)
        {
            try
            {
                string safe = string.IsNullOrEmpty(purpose) ? "job" : purpose;
                if (!Directory.Exists(WorkDir)) Directory.CreateDirectory(WorkDir);
                string path = Path.Combine(WorkDir, safe + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(path);
                return path;
            }
            catch
            {
                return null;
            }
        }

        // DirectoryNotFoundException / FileNotFoundException mean "the entry is
        // not there"; for cleanup that is the same state a successful delete
        // would have produced (helper taken from the source project).
        public static bool IsMissingPath(Exception ex)
        {
            return ex is DirectoryNotFoundException || ex is FileNotFoundException;
        }

        // Removes the session work dir after the process finishes a download
        // (or at close). Never deletes anything outside WorkDir.
        public static void CleanupSessionWorkDir()
        {
            try
            {
                string root = Path.GetFullPath(WorkDir);
                string dir = Path.GetFullPath(SessionWorkDir);
                if (dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { }
        }
    }
}
