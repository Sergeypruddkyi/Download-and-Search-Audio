using System;
using System.IO;
using System.Text;

namespace DownloadSearchAudio
{
    // Minimal sanitized diagnostic log. The user-facing dialogs stay short
    // (see MainForm), while the technical details - failure classification and
    // the yt-dlp error tail - are written here WITHOUT secret data: every line
    // passes DiagnosticSanitizer, and cookie values are never read anywhere.
    //
    // File: <application folder>\data\logs\yt-diag.log (append-only, portable
    // data tree). Rotated into yt-diag.1.log when it exceeds the size cap.
    // All IO failures are swallowed: logging must never break the download.
    internal static class DiagLog
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Sync = new object();

        public static void Event(string operation, string text)
        {
            try
            {
                lock (Sync)
                {
                    string dir = Path.Combine(AppPaths.DataDir, "logs");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "yt-diag.log");

                    try
                    {
                        if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                        {
                            string old = Path.Combine(dir, "yt-diag.1.log");
                            try { if (File.Exists(old)) File.Delete(old); } catch { }
                            File.Move(path, old);
                        }
                    }
                    catch { }

                    string message = (text ?? "").Replace("\r", "");
                    string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z";
                    File.AppendAllText(path, stamp + " [" + operation + "] " + DiagnosticSanitizer.Sanitize(message) + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
