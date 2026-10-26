using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DownloadSearchAudio
{
    // Audio-only yt-dlp runner, adapted from YouTubeDownloader-Net10
    // src\YtDlpRunner.cs. Removed: quality ladder, video formats, diagnostics
    // sink (a smaller DiagLog is used instead), self-update. Kept: process
    // environment (TEMP/TMP inside the portable data tree, DENO_DIR, PATH with
    // BaseDir first), piped UTF-8 output, taskkill /T /F cancel, progress
    // parsing, and the source project's cookie mechanism: the title probe and
    // the download can both carry a saved cookie jar (--cookies <staged copy>),
    // cookies come from a user-initiated browser export (CookieStore), never
    // from the jar of a first attempt.
    //
    // Format selection decision (no user quality/bitrate choice):
    //   - the yt-dlp selector is ALWAYS "bestaudio[ext=m4a]/bestaudio/best"
    //     (Best available audio);
    //   - the Format combo selects only the CONTAINER/post-processing:
    //       * MP3 (recommended)  -> -x --audio-format mp3 --audio-quality 0
    //         (transcoded by the bundled ffmpeg, widest compatibility);
    //       * Best available (original) -> no -x, the file keeps the original
    //         audio container (usually .m4a), no re-encoding loss.
    public class YtLine
    {
        public string Text;
    }

    public class YtRunResult
    {
        public int ExitCode;
        public string Output;
    }

    public enum AudioOutputMode
    {
        Original, // keep the source audio container
        Mp3       // extract and transcode to MP3
    }

    public static class AudioOutputModeIo
    {
        public static AudioOutputMode Parse(string value)
        {
            if (string.Equals(value, "mp3", StringComparison.OrdinalIgnoreCase)) return AudioOutputMode.Mp3;
            return AudioOutputMode.Original;
        }

        public static string ToSetting(AudioOutputMode mode)
        {
            return mode == AudioOutputMode.Mp3 ? "mp3" : "original";
        }
    }

    public enum EngineFailureKind
    {
        None,
        InvalidUrl,
        Unavailable,
        AccessDenied,
        BotCheck,
        RateLimited,
        Network,
        Unsupported,
        Timeout,
        Unknown
    }

    public static class OutputParser
    {
        private static readonly Regex RxPercent = new Regex(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
        private static readonly Regex RxSpeed = new Regex(@"at\s+(?<s>[\d.]+\s*[KMGT]?i?B/s|Unknown\s+B/s)", RegexOptions.Compiled);
        private static readonly Regex RxEta = new Regex(@"ETA\s+(?<e>\d+:\d\d)", RegexOptions.Compiled);
        private static readonly Regex RxDest = new Regex(@"\[download\]\s+Destination:\s+(?<f>.+)", RegexOptions.Compiled);
        private static readonly Regex RxExtractDest = new Regex(@"\[ExtractAudio\]\s+Destination:\s+(?<f>.+)", RegexOptions.Compiled);
        private static readonly Regex RxAlready = new Regex(@"has already been downloaded", RegexOptions.Compiled);

        public static double? TryPercent(string line)
        {
            Match m = RxPercent.Match(line);
            if (!m.Success) return null;
            double d;
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        public static string TrySpeed(string line)
        {
            Match m = RxSpeed.Match(line);
            return m.Success ? m.Groups["s"].Value : null;
        }

        public static string TryEta(string line)
        {
            Match m = RxEta.Match(line);
            return m.Success ? m.Groups["e"].Value : null;
        }

        public static string TryDestination(string line)
        {
            Match m = RxDest.Match(line);
            return m.Success ? m.Groups["f"].Value.Trim() : null;
        }

        public static string TryExtractAudioDestination(string line)
        {
            Match m = RxExtractDest.Match(line);
            return m.Success ? m.Groups["f"].Value.Trim() : null;
        }

        public static bool IsAlreadyDownloaded(string line)
        {
            return RxAlready.IsMatch(line);
        }

        public static bool IsFetchingInfo(string line)
        {
            return line != null && (line.StartsWith("[youtube]") || line.StartsWith("[info]"));
        }
    }

    public static class YtDlpRunner
    {
        public static string Quote(string arg)
        {
            if (arg == null) arg = "";
            bool need = arg.Length == 0;
            foreach (char ch in arg)
            {
                if (ch <= ' ' || ch == '"')
                {
                    need = true;
                    break;
                }
            }
            if (!need) return arg;

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char ch in arg)
            {
                if (ch == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (ch == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    if (backslashes > 0)
                    {
                        sb.Append('\\', backslashes);
                        backslashes = 0;
                    }
                    sb.Append(ch);
                }
            }
            if (backslashes > 0) sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string FormatArgs(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        private static ProcessStartInfo BuildStartInfo(string arguments)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = AppPaths.YtExe;
            psi.Arguments = arguments;
            psi.WorkingDirectory = AppPaths.BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.EnvironmentVariables["PATH"] = AppPaths.BaseDir.TrimEnd('\\') + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
            psi.EnvironmentVariables["TEMP"] = AppPaths.SessionWorkDir;
            psi.EnvironmentVariables["TMP"] = AppPaths.SessionWorkDir;
            psi.EnvironmentVariables["DENO_DIR"] = Path.Combine(AppPaths.DataDir, "cache", "deno");
            return psi;
        }

        public const string AudioFormatSpec = "bestaudio[ext=m4a]/bestaudio/best";

        public static string[] AudioDownloadArgs(string folder, string url, AudioOutputMode mode)
        {
            string escaped = folder.Replace("%", "%%");
            if (!escaped.EndsWith("\\")) escaped += "\\";
            string outTemplate = escaped + "%(title)s.%(ext)s";
            List<string> args = new List<string>
            {
                "--newline",
                "--color", "no_color",
                "--encoding", "utf-8",
                "--ignore-config",
                "--no-cache-dir",
                "--no-playlist",
                "--windows-filenames",
                "--js-runtimes", "deno:" + AppPaths.DenoExe,
                "-f", AudioFormatSpec
            };
            if (mode == AudioOutputMode.Mp3)
            {
                args.Add("-x");
                args.Add("--audio-format");
                args.Add("mp3");
                args.Add("--audio-quality");
                args.Add("0");
            }
            args.Add("--ffmpeg-location");
            args.Add(AppPaths.FfmpegExe);
            // Never overwrite an existing user file: yt-dlp skips/renames, our
            // pre-download duplicate check is the first line of defense.
            args.Add("--no-overwrites");
            args.Add("-o");
            args.Add(outTemplate);
            args.Add(url);
            return args.ToArray();
        }

        // Metadata probe WITHOUT downloading: prints title/duration/approximate
        // size for the format the download would actually pick. Key=value form
        // for every field, parsed by AudioMetadataParser (unit-testable).
        public static string[] AudioTitleArgs(string url)
        {
            return new[]
            {
                "--ignore-config", "--no-cache-dir", "--encoding", "utf-8", "--color", "no_color",
                "--no-playlist",
                "--js-runtimes", "deno:" + AppPaths.DenoExe,
                "-f", AudioFormatSpec,
                "--print", "title=%(title)s",
                "--print", "duration=%(duration)s",
                "--print", "size_approx=%(filesize_approx)s",
                url
            };
        }

        // Arguments that export a browser's cookies into a yt-dlp cookie jar.
        // Adapted from YouTubeDownloader-Net10 src\YtDlpRunner.cs
        // (SaveCookiesArgs). yt-dlp merges "cookies-from-browser" into the
        // "--cookies" jar and dumps that jar on close, so a single run both
        // loads the browser cookies and writes them to the given file. No URL
        // is supplied on purpose: the jar is written on close regardless, and
        // this keeps the operation fully offline (no request to YouTube).
        // Callers MUST verify the produced file (CookieStore.AnalyzeJar).
        //
        // browser is a yt-dlp browser name (firefox/chrome/edge/brave/chromium/
        // vivaldi/opera). The profile is left to yt-dlp's own default, so no
        // browser profile is ever enumerated or modified by the app.
        public static string[] SaveCookiesArgs(string cookieFile, string browser)
        {
            return new[]
            {
                "--ignore-config",
                "--encoding", "utf-8",
                "--color", "no_color",
                "--no-warnings",
                "--no-cache-dir",
                "--cookies-from-browser", string.IsNullOrEmpty(browser) ? "firefox" : browser,
                "--cookies", cookieFile
            };
        }

        // Returns args with "--cookies <file>" inserted before the URL (the
        // last element). Every other parameter stays identical, so a cookie
        // attempt differs from the plain attempt EXCLUSIVELY by the
        // authentication method (same invariant as the source project).
        public static string[] WithCookies(string[] args, string cookieFile)
        {
            List<string> withCookies = new List<string>(args);
            int urlIndex = withCookies.Count - 1;
            withCookies.Insert(urlIndex, "--cookies");
            withCookies.Insert(urlIndex + 1, cookieFile);
            return withCookies.ToArray();
        }

        public class AudioTitleInfo
        {
            public string Title;
            public double? DurationSeconds;
            public long? SizeApproxBytes;
        }

        public static long? ParseSizeValue(string value)
        {
            if (value == null) return null;
            string v = value.Trim();
            if (v.Length == 0) return null;
            if (string.Equals(v, "NA", StringComparison.OrdinalIgnoreCase)) return null;
            long n;
            if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0) return n;
            return null;
        }

        // Parses the stdout of AudioTitleArgs. Separated from the process
        // plumbing so it is unit-testable without yt-dlp or a network.
        public static class AudioMetadataParser
        {
            public static AudioTitleInfo Parse(string stdout)
            {
                if (stdout == null) return null;
                string title = null;
                double? duration = null;
                long? size = null;
                foreach (string rawLine in stdout.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("title=", StringComparison.Ordinal) && title == null)
                    {
                        title = line.Substring(6).Trim();
                        // yt-dlp prints "title=NA" only when the field is empty;
                        // an empty title is treated as a failed probe.
                        if (string.Equals(title, "NA", StringComparison.OrdinalIgnoreCase)) title = null;
                    }
                    else if (line.StartsWith("duration=", StringComparison.Ordinal) && !duration.HasValue)
                    {
                        string v = line.Substring(9).Trim();
                        double d;
                        if (!string.Equals(v, "NA", StringComparison.OrdinalIgnoreCase)
                            && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                            && d >= 0)
                            duration = d;
                    }
                    else if (line.StartsWith("size_approx=", StringComparison.Ordinal) && !size.HasValue)
                    {
                        size = ParseSizeValue(line.Substring(12));
                    }
                }
                if (string.IsNullOrEmpty(title)) return null;
                return new AudioTitleInfo { Title = title, DurationSeconds = duration, SizeApproxBytes = size };
            }
        }

        // Metadata probe (synchronous, run from a worker thread). Cookie order
        // is the source project's (YtDlpRunner.TryGetTitleAndSizeLocked): a
        // saved jar is the known-good path (YouTube answers the anonymous
        // request with 429 / bot-check), so it is used for the FIRST attempt
        // and the anonymous attempt is only the bounded fallback when the jar
        // itself looks broken. Without a jar the anonymous attempt runs first
        // and at most ONE cookie attempt may follow for a cookie-fixable
        // failure. Either way the probe costs at most two yt-dlp runs - never
        // an unbounded retry loop.
        public static bool TryGetAudioTitle(string url, out AudioTitleInfo info, out string output)
        {
            info = null;
            output = null;
            string[] args = AudioTitleArgs(url);

            if (CookieStore.HasUsableCookies())
            {
                AudioTitleInfo attempt;
                string attemptOutput;
                using (StagedCookieFile staged = CookieStore.StageUsableCopy())
                {
                    if (staged == null)
                    {
                        // No private copy could be prepared; run the stock
                        // anonymous probe rather than touching the saved jar.
                        bool ok = RunTitle(args, out attempt, out attemptOutput);
                        output = attemptOutput;
                        info = attempt;
                        return ok;
                    }
                    if (RunTitle(WithCookies(args, staged.FilePath), out attempt, out attemptOutput))
                    {
                        info = attempt;
                        output = attemptOutput;
                        return true;
                    }
                    output = attemptOutput;
                }
                // The jar itself may be expired or unreadable: fall back to the
                // anonymous request only for failures an anonymous run can fix.
                if (ShouldRetryTitleWithoutCookies(Classify(output), output))
                {
                    AudioTitleInfo fallback;
                    string fallbackOutput;
                    if (RunTitle(args, out fallback, out fallbackOutput))
                    {
                        info = fallback;
                        output = fallbackOutput;
                        return true;
                    }
                }
                return false;
            }

            DiagLog.Event("probe", "no usable cookie jar for " + url);
            if (RunTitle(args, out info, out output)) return true;
            if (!CookiesMayHelpProbe(Classify(output), output)) return false;
            // A metadata probe can receive only HTTP 429 even when the saved
            // session is known to work for the same video download. Keep the
            // retry bounded to one cookie-backed metadata request.
            if (!CookieStore.HasUsableCookies()) return false;
            using (StagedCookieFile staged = CookieStore.StageUsableCopy())
            {
                if (staged == null) return false;
                AudioTitleInfo retry;
                string retryOutput;
                if (RunTitle(WithCookies(args, staged.FilePath), out retry, out retryOutput))
                {
                    info = retry;
                    output = retryOutput;
                    return true;
                }
                return false;
            }
        }

        // One metadata run: unchanged from the previous single-shot probe apart
        // from the sanitized diagnostic logging.
        private static bool RunTitle(string[] args, out AudioTitleInfo info, out string output)
        {
            info = null;
            output = null;
            DiagLog.Event("probe", "start: args=" + FormatArgs(args));
            try
            {
                using (Process p = Process.Start(BuildStartInfo(FormatArgs(args))))
                {
                    StringBuilder so = new StringBuilder();
                    StringBuilder se = new StringBuilder();
                    PipeDrainCounter drain = new PipeDrainCounter();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) { lock (so) { so.AppendLine(e.Data); } return; }
                        drain.OnData(s, e);
                    };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) { lock (se) { se.AppendLine(e.Data); } return; }
                        drain.OnData(s, e);
                    };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    drain.WaitDrained(5000);
                    int exitCode = p.ExitCode;
                    string outText, errText;
                    lock (so) { outText = so.ToString(); }
                    lock (se) { errText = se.ToString(); }
                    output = outText + "\n" + errText;
                    DiagLog.Event("probe", "exit: code=" + exitCode + " category="
                        + (exitCode == 0 ? "None" : Classify(output).ToString())
                        + "\n" + output);
                    if (exitCode != 0) return false;
                    info = AudioMetadataParser.Parse(outText);
                    return info != null && !string.IsNullOrEmpty(info.Title);
                }
            }
            catch (Exception ex)
            {
                DiagLog.Event("probe", "start failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // Decision seam for the fallback after a cookie-backed probe failed.
        // Retrying anonymously cannot fix a rate limit or a network error, so
        // only failures that point at the cookie jar itself (rejected session,
        // unreadable/expired jar) trigger the second run. Adapted from the
        // source project (YtDlpRunner.ShouldRetryTitleWithoutCookies).
        public static bool ShouldRetryTitleWithoutCookies(EngineFailureKind failure, string output)
        {
            if (failure == EngineFailureKind.AccessDenied) return true;
            string text = output ?? "";
            return text.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Decision seam for the anonymous-first probe (network-free, unit
        // tested): an explicit YouTube access/bot/age signal must be present
        // before a cookie attempt costs a second process. A metadata probe may
        // receive ONLY HTTP 429 even when downloads later succeed (source
        // comment), so a bare 429 also justifies exactly one cookie attempt
        // here. Adapted from EngineRetryPolicy.CookiesMayHelp + the probe
        // special-case in the source YtDlpRunner.
        public static bool CookiesMayHelpProbe(EngineFailureKind failure, string output)
        {
            if (failure == EngineFailureKind.AccessDenied
                || failure == EngineFailureKind.BotCheck) return true;
            if (failure == EngineFailureKind.RateLimited) return true;
            string text = output ?? "";
            return ContainsMarker(text, "not a bot")
                || ContainsMarker(text, "sign in")
                || ContainsMarker(text, "login_required")
                || ContainsMarker(text, "confirm your age")
                || ContainsMarker(text, "cookies-from-browser")
                || ContainsMarker(text, "--cookies");
        }

        // Strict version for the DOWNLOAD reserve attempt: a bare HTTP 429
        // (no "not a bot" / "sign in" text) is a transient throttle, not an
        // authorization problem, and must NOT trigger a cookie retry (same
        // policy as the source EngineRetryPolicy.CookiesMayHelp).
        public static bool CookiesMayHelpDownload(EngineFailureKind failure, string output)
        {
            if (failure == EngineFailureKind.AccessDenied
                || failure == EngineFailureKind.BotCheck) return true;
            if (failure != EngineFailureKind.RateLimited
                && failure != EngineFailureKind.Unsupported
                && failure != EngineFailureKind.Unknown)
                return false;
            string text = output ?? "";
            return ContainsMarker(text, "not a bot")
                || ContainsMarker(text, "sign in")
                || ContainsMarker(text, "login_required")
                || ContainsMarker(text, "confirm your age")
                || ContainsMarker(text, "cookies-from-browser")
                || ContainsMarker(text, "--cookies");
        }

        private static bool ContainsMarker(string text, string marker)
        {
            return text != null && text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static Task<YtRunResult> RunAsync(string[] args, Action<YtLine> onLine, Action<Process> onStarted)
        {
            Process proc = new Process();
            proc.StartInfo = BuildStartInfo(FormatArgs(args));
            proc.EnableRaisingEvents = true;

            TaskCompletionSource<YtRunResult> tcs = new TaskCompletionSource<YtRunResult>();
            StringBuilder output = new StringBuilder();
            object sync = new object();
            PipeDrainCounter drain = new PipeDrainCounter();

            DataReceivedEventHandler outH = delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) { drain.OnData(s, e); return; }
                lock (sync) { output.AppendLine(e.Data); }
                if (onLine != null) onLine(new YtLine { Text = e.Data });
            };
            DataReceivedEventHandler errH = delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) { drain.OnData(s, e); return; }
                lock (sync) { output.AppendLine(e.Data); }
                if (onLine != null) onLine(new YtLine { Text = e.Data });
            };

            proc.OutputDataReceived += outH;
            proc.ErrorDataReceived += errH;
            proc.Exited += delegate(object s, EventArgs e)
            {
                Task.Run(delegate
                {
                    try { proc.WaitForExit(); } catch { }
                    drain.WaitDrained(10000);
                    int code = 0;
                    try { code = proc.ExitCode; } catch { }
                    string text;
                    lock (sync) { text = output.ToString(); }
                    try { proc.Dispose(); } catch { }
                    tcs.TrySetResult(new YtRunResult { ExitCode = code, Output = text });
                });
            };

            proc.Start();
            if (onStarted != null) onStarted(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return tcs.Task;
        }

        public static void KillTree(Process proc)
        {
            if (proc == null) return;
            try { if (proc.HasExited) return; }
            catch { return; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/PID " + proc.Id + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process tk = Process.Start(psi))
                {
                    tk.WaitForExit(5000);
                }
            }
            catch
            {
                try { proc.Kill(); }
                catch { }
            }
        }

        internal sealed class PipeDrainCounter
        {
            private int _eofStreams;

            public void OnData(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) Interlocked.Increment(ref _eofStreams);
            }

            public bool Drained()
            {
                return Interlocked.CompareExchange(ref _eofStreams, 0, 0) == 2;
            }

            public bool WaitDrained(int timeoutMs)
            {
                for (int waitedMs = 0; waitedMs < timeoutMs; waitedMs += 50)
                {
                    if (Drained()) return true;
                    Thread.Sleep(50);
                }
                return Drained();
            }
        }

        // Coarse failure classification for user-facing messages (adapted from
        // the source project's YtDlpDownloadEngine.Classify / ErrorClassifier).
        public static EngineFailureKind Classify(string output)
        {
            string t = output ?? "";
            if (t.IndexOf("Unsupported URL", StringComparison.OrdinalIgnoreCase) >= 0) return EngineFailureKind.Unsupported;
            if (t.IndexOf("is not a valid URL", StringComparison.OrdinalIgnoreCase) >= 0) return EngineFailureKind.InvalidUrl;
            if (t.IndexOf("Sign in to confirm", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("confirm you're not a bot", StringComparison.OrdinalIgnoreCase) >= 0) return EngineFailureKind.BotCheck;
            if (t.IndexOf("private video", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Video unavailable", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("members-only", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("age", StringComparison.OrdinalIgnoreCase) >= 0
                   && t.IndexOf("confirm", StringComparison.OrdinalIgnoreCase) >= 0) return EngineFailureKind.Unavailable;
            if (t.IndexOf("HTTP Error 429", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Too Many Requests", StringComparison.OrdinalIgnoreCase) >= 0) return EngineFailureKind.RateLimited;
            if (t.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("getaddrinfo", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Connection refused", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Connection reset", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("network", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("unable to download", StringComparison.OrdinalIgnoreCase) >= 0) return EngineFailureKind.Network;
            return EngineFailureKind.Unknown;
        }

        public static string FailureText(EngineFailureKind kind)
        {
            switch (kind)
            {
                case EngineFailureKind.Unsupported: return "This link is not supported by the downloader.";
                case EngineFailureKind.InvalidUrl: return "The link is not a valid YouTube URL.";
                case EngineFailureKind.BotCheck: return "YouTube is asking to confirm you are not a bot or to sign in. Saving YouTube cookies from your browser (the Cookies button) can help, but it cannot be guaranteed - try again later if it keeps failing.";
                case EngineFailureKind.Unavailable: return "The video is unavailable (private, removed or restricted).";
                case EngineFailureKind.RateLimited: return "YouTube temporarily limited this request (HTTP 429: too many requests). No more requests should be made right now - wait a while, then try again. Cookies help with sign-in checks but cannot guarantee a rate limit goes away.";
                case EngineFailureKind.Network: return "Network error. Check the internet connection.";
                case EngineFailureKind.Timeout: return "The operation timed out.";
                default: return "The download failed. See the status line for details.";
            }
        }
    }
}
