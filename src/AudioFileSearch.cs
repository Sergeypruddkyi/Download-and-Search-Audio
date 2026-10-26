using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;

namespace DownloadSearchAudio
{
    public enum AudioFileSearchStatus
    {
        Found,
        NotFound,
        MultipleMatches,
        EmptyQuery,
        FolderUnavailable,
        ScanError
    }

    public sealed class AudioFileSearchResult
    {
        public AudioFileSearchStatus Status { get; private set; }
        public string FilePath { get; private set; }
        public string[] FilePaths { get; private set; }
        public string Error { get; private set; }

        private AudioFileSearchResult(AudioFileSearchStatus status, string[] filePaths, string error)
        {
            Status = status;
            FilePaths = filePaths ?? Array.Empty<string>();
            FilePath = FilePaths.Length > 0 ? FilePaths[0] : null;
            Error = error;
        }

        internal static AudioFileSearchResult Create(AudioFileSearchStatus status, string[] filePaths, string error)
        {
            return new AudioFileSearchResult(status, filePaths, error);
        }
    }

    public static class AudioFileSearch
    {
        public static AudioFileSearchResult Search(string folder, string query, bool includeSubfolders)
        {
            string term = query == null ? "" : query.Trim();
            if (term.Length == 0)
                return AudioFileSearchResult.Create(AudioFileSearchStatus.EmptyQuery, null, null);

            if (string.IsNullOrWhiteSpace(folder))
                return AudioFileSearchResult.Create(AudioFileSearchStatus.FolderUnavailable, null, null);

            string root;
            try { root = Path.GetFullPath(folder.Trim()); }
            catch (Exception ex) when (IsInvalidPathError(ex))
            {
                return AudioFileSearchResult.Create(AudioFileSearchStatus.FolderUnavailable, null, ex.Message);
            }

            if (!Directory.Exists(root))
                return AudioFileSearchResult.Create(AudioFileSearchStatus.FolderUnavailable, null, null);

            // Force one top-level enumeration with IgnoreInaccessible disabled;
            // otherwise an inaccessible selected root could look like no matches.
            try
            {
                using (var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator())
                    entries.MoveNext();
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                return AudioFileSearchResult.Create(AudioFileSearchStatus.FolderUnavailable, null, ex.Message);
            }

            List<string> matches = new List<string>();
            try
            {
                EnumerationOptions options = new EnumerationOptions
                {
                    RecurseSubdirectories = includeSubfolders,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    ReturnSpecialDirectories = false
                };

                foreach (string path in Directory.EnumerateFiles(root, "*", options))
                {
                    if (!DuplicateCheck.AudioExtensions.Contains(Path.GetExtension(path))) continue;
                    if (Path.GetFileName(path).IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    matches.Add(path);
                }
            }
            catch (Exception ex) when (IsAccessError(ex) || ex is IOException || ex is SecurityException)
            {
                if (!Directory.Exists(root))
                    return AudioFileSearchResult.Create(AudioFileSearchStatus.FolderUnavailable, null, ex.Message);
                return AudioFileSearchResult.Create(AudioFileSearchStatus.ScanError, null, ex.Message);
            }

            if (matches.Count == 0)
                return AudioFileSearchResult.Create(AudioFileSearchStatus.NotFound, null, null);
            if (matches.Count == 1)
                return AudioFileSearchResult.Create(AudioFileSearchStatus.Found, matches.ToArray(), null);
            return AudioFileSearchResult.Create(AudioFileSearchStatus.MultipleMatches, matches.ToArray(), null);
        }

        private static bool IsInvalidPathError(Exception ex)
        {
            return ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException;
        }

        private static bool IsAccessError(Exception ex)
        {
            return ex is UnauthorizedAccessException || ex is DirectoryNotFoundException || ex is FileNotFoundException;
        }
    }

    public static class AudioDurationProbe
    {
        public static bool TryGetDuration(string ffprobePath, string audioPath, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffprobePath)
                || string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
                return false;

            try
            {
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("-v");
                start.ArgumentList.Add("error");
                start.ArgumentList.Add("-show_entries");
                start.ArgumentList.Add("format=duration");
                start.ArgumentList.Add("-of");
                start.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
                start.ArgumentList.Add(audioPath);

                using (Process process = Process.Start(start))
                {
                    if (process == null) return false;
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(true); } catch { }
                        return false;
                    }

                    string output = outputTask.GetAwaiter().GetResult();
                    errorTask.GetAwaiter().GetResult();
                    return process.ExitCode == 0 && TryParseDuration(output, out duration);
                }
            }
            catch
            {
                duration = TimeSpan.Zero;
                return false;
            }
        }

        public static bool TryParseDuration(string output, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(output)) return false;

            string value = output.Trim();
            int newline = value.IndexOfAny(new[] { '\r', '\n' });
            if (newline >= 0) value = value.Substring(0, newline).Trim();

            double seconds;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                return false;

            try
            {
                duration = TimeSpan.FromSeconds(seconds);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }
}
