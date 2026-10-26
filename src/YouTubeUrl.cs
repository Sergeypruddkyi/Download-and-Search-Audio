using System;
using System.Text.RegularExpressions;

namespace DownloadSearchAudio
{
    // YouTube URL validation, copied from YouTubeDownloader-Net10 src\YouTubeUrl.cs
    // (only the namespace changed). Accepts watch/shorts/live/embed/youtu.be
    // forms with 11-char video ids.
    public static class YouTubeUrl
    {
        private static readonly Regex Rx = new Regex(
            @"^(?:https?://)?(?:www\.|m\.|music\.)?(?:(?:youtube\.com|youtube-nocookie\.com)/(?:watch\?(?:[^#&]*&)*v=(?:[\w-]{11})(?:[&][^ ]*)?|watch/(?:[\w-]{11})(?:[/?][^ ]*)?|shorts/(?:[\w-]{11})(?:[/?][^ ]*)?|live/(?:[\w-]{11})(?:[/?][^ ]*)?|embed/(?:[\w-]{11})(?:[/?][^ ]*)?|v/(?:[\w-]{11})(?:[/?][^ ]*)?)|youtu\.be/(?:[\w-]{11})(?:[?/][^ ]*)?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsValid(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string t = url.Trim();
            foreach (char ch in t)
            {
                if (ch <= ' ' || ch == '"' || ch == '\\') return false;
            }
            return Rx.IsMatch(t);
        }
    }
}
