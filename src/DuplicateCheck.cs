using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DownloadSearchAudio
{
    // Pre-download duplicate protection (REQUIRED before any yt-dlp run).
    //
    // The future file's name is "%(title)s.%(ext)s" produced by yt-dlp, so the
    // only reliably known part before the download is the TITLE. The pre-download
    // check therefore matches by NAME in the save folder (recursively, including
    // subfolders). Size/duration are deliberately NOT used pre-download: the
    // real byte size of the transcoded output is unknown until the download
    // finishes, and an equal size alone never proves identical audio.
    //
    // Matching rule (documented, unit-testable):
    //   1. exact      - normalized(full title) == normalized(existing name
    //                   without extension);
    //   2. decoration - the bracket-decorated forms are equal, i.e. the names
    //                   differ only in "(...)" / "[...]" groups such as
    //                   "(Official Video)" / "[HD]"; e.g. the collection has
    //                   "Song (Official Video)" and YouTube returns
    //                   "Song (Lyric Video)" -> both strip to "song" -> match.
    // Pure partial ("contains") matches NEVER block a download - a partial
    // match can easily be a different work with a similar name.
    //
    // Behavior contract:
    //   FolderNotSpecified/FolderMissing/ScanError -> download MUST NOT start;
    //   MatchesFound                              -> download MUST NOT start;
    //   NoMatches                                 -> download may start.
    public enum DuplicateCheckStatus
    {
        FolderNotSpecified,
        FolderMissing,
        ScanError,
        NoMatches,
        MatchesFound
    }

    public class DuplicateCheckResult
    {
        public DuplicateCheckStatus Status;
        public List<string> MatchPaths = new List<string>();
        public string Error; // filled for ScanError
        public int ScannedFiles;
    }

    public static class DuplicateCheck
    {
        // Audio extensions considered during the scan. The future download is
        // audio, so only audio files are compared against.
        public static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".m4a", ".wav", ".flac", ".aac", ".ogg", ".opus", ".wma", ".webm"
        };

        // Fallback used when yt-dlp could not tell the format: check any file
        // with a matching name regardless of extension? No - keep audio-only,
        // documented, so e.g. a text file named like the song does not block.
        public static string Normalize(string name)
        {
            if (name == null) return "";
            StringBuilder sb = new StringBuilder(name.Length);
            bool lastWasSpace = false;
            foreach (char ch in name.ToLowerInvariant())
            {
                // Every non-alphanumeric character (punctuation, separators AND
                // yt-dlp's fullwidth replacement lookalikes such as U+29F8 for
                // "/") normalizes to a single space, so the comparison is
                // invariant to filename sanitization. Letters and digits of
                // ANY script (e.g. Cyrillic, Hangul) are preserved.
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            return sb.ToString().Trim();
        }

        // Removes bracketed decoration groups "(...)" / "[...]" (repeatedly,
        // innermost first) and normalizes the remainder.
        public static string StripDecorations(string name)
        {
            string s = name ?? "";
            for (int guard = 0; guard < 8; guard++)
            {
                int open1 = s.IndexOf('(');
                int close1 = s.IndexOf(')');
                int open2 = s.IndexOf('[');
                int close2 = s.IndexOf(']');
                int open = -1, close = -1;
                if (open1 >= 0 && (open2 < 0 || open1 < open2)) { open = open1; close = close1; }
                else if (open2 >= 0) { open = open2; close = close2; }
                if (open < 0 || close <= open) break;
                s = s.Substring(0, open) + s.Substring(close + 1);
            }
            return Normalize(s);
        }

        public static bool IsMatch(string existingFileNameWithoutExt, string youTubeTitle)
        {
            string nExisting = Normalize(existingFileNameWithoutExt);
            string nTitle = Normalize(youTubeTitle);
            if (nExisting.Length == 0 || nTitle.Length == 0) return false;
            if (string.Equals(nExisting, nTitle, StringComparison.Ordinal)) return true;
            string dExisting = StripDecorations(existingFileNameWithoutExt);
            string dTitle = StripDecorations(youTubeTitle);
            if (dExisting.Length > 0 && dTitle.Length > 0
                && string.Equals(dExisting, dTitle, StringComparison.Ordinal))
                return true;
            return false;
        }

        public static DuplicateCheckResult CheckFolder(string saveFolder, string youTubeTitle, bool includeSubfolders)
        {
            DuplicateCheckResult r = new DuplicateCheckResult();
            if (string.IsNullOrWhiteSpace(saveFolder))
            {
                r.Status = DuplicateCheckStatus.FolderNotSpecified;
                return r;
            }
            if (!Directory.Exists(saveFolder))
            {
                r.Status = DuplicateCheckStatus.FolderMissing;
                return r;
            }
            try
            {
                // Root readability check WITHOUT IgnoreInaccessible: if the
                // root itself cannot be listed the check did NOT run, so the
                // download must not start (ScanError), never a silent "no
                // matches". Nested entries stay fault-tolerant below.
                foreach (string probe in Directory.EnumerateFiles(saveFolder)) break;

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = includeSubfolders,
                    // Unreadable subfolders are skipped (same policy as the
                    // project's search design); a failure of the ROOT folder
                    // itself throws and is reported as ScanError.
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (string path in Directory.EnumerateFiles(saveFolder, "*", options))
                {
                    r.ScannedFiles++;
                    string ext = Path.GetExtension(path);
                    if (!AudioExtensions.Contains(ext)) continue;
                    string nameNoExt = Path.GetFileNameWithoutExtension(path);
                    if (IsMatch(nameNoExt, youTubeTitle))
                        r.MatchPaths.Add(path);
                }
                r.Status = r.MatchPaths.Count > 0 ? DuplicateCheckStatus.MatchesFound : DuplicateCheckStatus.NoMatches;
            }
            catch (Exception ex)
            {
                r.Status = DuplicateCheckStatus.ScanError;
                r.Error = ex.Message;
            }
            return r;
        }
    }
}
