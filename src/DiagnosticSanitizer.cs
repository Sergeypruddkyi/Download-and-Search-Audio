using System;
using System.Text.RegularExpressions;

namespace DownloadSearchAudio
{
    // Pure string redaction, no state. Adapted from YouTubeDownloader-Net10
    // src\VideoDownloadEngine.cs (DiagnosticSanitizer). Applied to every text
    // that is logged or shown outside the controlled UI paths so cookie
    // headers, authorization lines and URL query secrets never leave the
    // process boundary nor enter the diagnostic log.
    internal static class DiagnosticSanitizer
    {
        private static readonly Regex QuerySecrets = new Regex(@"(?i)([?&](?:sig|signature|token|pot|po_token|lsig|expire|n)=)[^&#\s]+",
            RegexOptions.Compiled);
        private static readonly Regex CookieLines = new Regex(@"(?im)^.*(?:cookie|authorization):.*$", RegexOptions.Compiled);

        public static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return CookieLines.Replace(QuerySecrets.Replace(text, "$1[redacted]"), "[sensitive header redacted]");
        }
    }
}
