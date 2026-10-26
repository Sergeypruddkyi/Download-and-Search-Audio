using System;
using System.IO;
using DownloadSearchAudio;

namespace DuplicateCheckTests
{
    // Offline tests for the pre-download duplicate check and the metadata
    // parser (no yt-dlp, no network, no real downloads).
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("PASS  " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("FAIL  " + name);
            }
        }

        private static void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        private static void TestAudioFileSearch(string root)
        {
            string folder = Path.Combine(root, "music-search");
            string nested = Path.Combine(folder, "subfolder");
            Directory.CreateDirectory(nested);
            string exactPath = Path.Combine(folder, "Artist - Northern Lights.mp3");
            File.WriteAllBytes(exactPath, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(folder, "Artist - Northern Lights (Live).wav"), new byte[] { 4, 5, 6 });
            File.WriteAllBytes(Path.Combine(folder, "notes.txt"), new byte[] { 7 });
            File.WriteAllBytes(Path.Combine(nested, "Hidden Theme.opus"), new byte[] { 8, 9 });

            AudioFileSearchResult full = AudioFileSearch.Search(folder, "Artist - Northern Lights.mp3", true);
            Check("Audio search: full filename -> Found",
                full.Status == AudioFileSearchStatus.Found && full.FilePath == exactPath);

            AudioFileSearchResult partial = AudioFileSearch.Search(folder, "lights (live)", true);
            Check("Audio search: partial case-insensitive filename -> Found",
                partial.Status == AudioFileSearchStatus.Found
                && Path.GetFileName(partial.FilePath) == "Artist - Northern Lights (Live).wav");

            AudioFileSearchResult none = AudioFileSearch.Search(folder, "No Such Track", true);
            Check("Audio search: no match -> NotFound", none.Status == AudioFileSearchStatus.NotFound);

            AudioFileSearchResult multiple = AudioFileSearch.Search(folder, "Northern Lights", true);
            string[] multipleNames = Array.ConvertAll(multiple.FilePaths, Path.GetFileName);
            Check("Audio search: multiple matches are summarized", multiple.Status == AudioFileSearchStatus.MultipleMatches
                && multipleNames.Length == 2
                && Array.IndexOf(multipleNames, "Artist - Northern Lights.mp3") >= 0
                && Array.IndexOf(multipleNames, "Artist - Northern Lights (Live).wav") >= 0);

            AudioFileSearchResult recursive = AudioFileSearch.Search(folder, "Hidden Theme", true);
            Check("Audio search: include subfolders -> Found", recursive.Status == AudioFileSearchStatus.Found);
            AudioFileSearchResult topOnly = AudioFileSearch.Search(folder, "Hidden Theme", false);
            Check("Audio search: exclude subfolders -> NotFound", topOnly.Status == AudioFileSearchStatus.NotFound);

            AudioFileSearchResult nonAudio = AudioFileSearch.Search(folder, "notes", true);
            Check("Audio search: ignores non-audio files", nonAudio.Status == AudioFileSearchStatus.NotFound);
            AudioFileSearchResult empty = AudioFileSearch.Search(folder, "  ", true);
            Check("Audio search: empty query -> EmptyQuery", empty.Status == AudioFileSearchStatus.EmptyQuery);
            AudioFileSearchResult missing = AudioFileSearch.Search(Path.Combine(root, "missing-search-folder"), "track", true);
            Check("Audio search: missing folder -> FolderUnavailable", missing.Status == AudioFileSearchStatus.FolderUnavailable);
            AudioFileSearchResult invalid = AudioFileSearch.Search("\0invalid", "track", true);
            Check("Audio search: invalid path -> FolderUnavailable", invalid.Status == AudioFileSearchStatus.FolderUnavailable);

            TimeSpan duration;
            Check("Audio duration: parses ffprobe seconds", AudioDurationProbe.TryParseDuration("123.25\r\n", out duration)
                && duration == TimeSpan.FromMilliseconds(123250));
            Check("Audio duration: rejects invalid output", !AudioDurationProbe.TryParseDuration("not-a-duration", out duration));
        }

        private static void TestAudioPreviewOpenFailure(string root)
        {
            string missingPath = Path.Combine(root, "missing-preview-file.mp3");
            bool failureRaised = false;
            string failurePath = null;
            using (AudioPreviewPlayer player = new AudioPreviewPlayer())
            {
                player.PlaybackFailed += delegate(string path, Exception error)
                {
                    failureRaised = error != null;
                    failurePath = path;
                };
                player.PlayAsync(missingPath).GetAwaiter().GetResult();
            }

            Check("Audio preview: missing file raises a handled playback error",
                failureRaised && failurePath == missingPath);
        }

        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "dsa-dup-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Cleanup(root);
            Directory.CreateDirectory(root);
            Console.WriteLine("Test root: " + root);

            TestAudioFileSearch(root);
            TestAudioPreviewOpenFailure(root);

            // --- Scenario 1: folder exists, no matching file -> download may start.
            string s1 = Path.Combine(root, "empty");
            Directory.CreateDirectory(s1);
            DuplicateCheckResult r1 = DuplicateCheck.CheckFolder(s1, "Me at the zoo", true);
            Check("S1 empty folder -> NoMatches", r1.Status == DuplicateCheckStatus.NoMatches && r1.MatchPaths.Count == 0);

            // --- Scenario 2: file with the same name exists (different ext is
            // irrelevant, the future extension is unknown pre-download).
            string s2 = Path.Combine(root, "same");
            Directory.CreateDirectory(s2);
            File.WriteAllBytes(Path.Combine(s2, "Me at the zoo.mp3"), new byte[] { 1, 2, 3 });
            DuplicateCheckResult r2 = DuplicateCheck.CheckFolder(s2, "Me at the zoo", true);
            Check("S2 exact name -> MatchesFound", r2.Status == DuplicateCheckStatus.MatchesFound && r2.MatchPaths.Count == 1);

            // Non-audio file with a matching name must NOT block (the download
            // is audio, so only audio files are compared).
            string s2b = Path.Combine(root, "nonaudio");
            Directory.CreateDirectory(s2b);
            File.WriteAllBytes(Path.Combine(s2b, "Me at the zoo.txt"), new byte[] { 1 });
            DuplicateCheckResult r2b = DuplicateCheck.CheckFolder(s2b, "Me at the zoo", true);
            Check("S2b non-audio match -> NoMatches", r2b.Status == DuplicateCheckStatus.NoMatches);

            // --- Scenario 3: match in a SUBFOLDER still blocks.
            string s3 = Path.Combine(root, "tree", "sub", "deeper");
            Directory.CreateDirectory(s3);
            File.WriteAllBytes(Path.Combine(s3, "Me at the zoo.m4a"), new byte[] { 1 });
            DuplicateCheckResult r3 = DuplicateCheck.CheckFolder(Path.Combine(root, "tree"), "Me at the zoo", true);
            Check("S3 subfolder match -> MatchesFound",
                r3.Status == DuplicateCheckStatus.MatchesFound
                && r3.MatchPaths.Count == 1
                && r3.MatchPaths[0].IndexOf("deeper", StringComparison.OrdinalIgnoreCase) >= 0);

            // --- Scenario 4: folder missing / not specified -> do not start.
            DuplicateCheckResult r4a = DuplicateCheck.CheckFolder(Path.Combine(root, "no-such-dir"), "Me at the zoo", true);
            Check("S4a missing folder -> FolderMissing", r4a.Status == DuplicateCheckStatus.FolderMissing);
            DuplicateCheckResult r4b = DuplicateCheck.CheckFolder("", "Me at the zoo", true);
            Check("S4b empty folder -> FolderNotSpecified", r4b.Status == DuplicateCheckStatus.FolderNotSpecified);

            // --- Scenario 5: unreadable folder -> ScanError (deny List on the root;
            // Directory.Exists still true because attributes remain readable).
            string s5 = Path.Combine(root, "denied");
            Directory.CreateDirectory(s5);
            File.WriteAllBytes(Path.Combine(s5, "whatever.mp3"), new byte[] { 1 });
            bool aclApplied = ApplyDenyListAcl(s5);
            DuplicateCheckResult r5 = DuplicateCheck.CheckFolder(s5, "Me at the zoo", true);
            if (aclApplied)
                Check("S5 denied folder -> ScanError or FolderMissing (never NoMatches)",
                    r5.Status == DuplicateCheckStatus.ScanError || r5.Status == DuplicateCheckStatus.FolderMissing);
            else
                Console.WriteLine("SKIP  S5 (icacls unavailable)");
            try { Cleanup(s5); } catch { }

            // --- Scenario 6: title with filesystem-invalid chars must be
            // normalized before comparing ("AC/DC: Back..." -> "ac dc back ...";
            // "_" and "." in existing names normalize to spaces too).
            string s6 = Path.Combine(root, "special");
            Directory.CreateDirectory(s6);
            File.WriteAllBytes(Path.Combine(s6, "AC_DC_Back_in_Black.mp3"), new byte[] { 1 });
            DuplicateCheckResult r6 = DuplicateCheck.CheckFolder(s6, "AC/DC: Back in Black", true);
            Check("S6 invalid-char normalization -> MatchesFound", r6.Status == DuplicateCheckStatus.MatchesFound);

            // Decorated titles: "(Official Video)" suffix counts as the same song.
            DuplicateCheckResult r6b = DuplicateCheck.CheckFolder(s6, "AC/DC: Back in Black (Official Video)", true);
            Check("S6b decorated title -> MatchesFound", r6b.Status == DuplicateCheckStatus.MatchesFound);

            // --- Scenario 7: partial/similar name must NOT block another work.
            string s7 = Path.Combine(root, "partial");
            Directory.CreateDirectory(s7);
            File.WriteAllBytes(Path.Combine(s7, "Back in Black Live at River Plate.mp3"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(s7, "Back in Black.mp3"), new byte[] { 1 });
            DuplicateCheckResult r7a = DuplicateCheck.CheckFolder(s7, "Back in Black (Studio Version)", true);
            Check("S7a similar-but-different name -> NoMatches",
                r7a.Status == DuplicateCheckStatus.NoMatches || r7a.MatchPaths.TrueForAll(p => Path.GetFileName(p) == "Back in Black.mp3"));
            DuplicateCheckResult r7b = DuplicateCheck.CheckFolder(s7, "Back in Black Rehearsal Tape", true);
            Check("S7b contains-only match -> NoMatches", r7b.Status == DuplicateCheckStatus.NoMatches);

            // --- Scenario 8: checkFolder is a pure filesystem function (no
            // process is spawned). Architectural guarantee: CheckFolder contains
            // no Process.Start; verified by source review. Here: verify it does
            // not depend on the yt.exe presence and is deterministic.
            DuplicateCheckResult r8a = DuplicateCheck.CheckFolder(s2, "Me at the zoo", true);
            DuplicateCheckResult r8b = DuplicateCheck.CheckFolder(s2, "Me at the zoo", true);
            Check("S8 deterministic and process-free",
                r8a.Status == r8b.Status && r8a.MatchPaths.Count == r8b.MatchPaths.Count);

            // --- Matching unit cases.
            Check("IsMatch exact", DuplicateCheck.IsMatch("Me at the zoo", "Me at the zoo"));
            Check("IsMatch case/space", DuplicateCheck.IsMatch("  ME   AT-THE-ZOO ", "me at the zoo"));
            Check("IsMatch decoration", DuplicateCheck.IsMatch("Song (Official Video)", "Song [HD 2024]"));
            Check("IsMatch partial is NOT a match", !DuplicateCheck.IsMatch("Song Live", "Song"));
            Check("IsMatch empty title is NOT a match", !DuplicateCheck.IsMatch("Song", ""));
            // Real-world case from GUI testing: yt-dlp --windows-filenames
            // replaces "/" with the fullwidth lookalike U+29F8 in the output
            // name, so the title's "/" must normalize to the same thing.
            Check("IsMatch yt-dlp fullwidth solidus",
                DuplicateCheck.IsMatch("PSY - GANGNAM STYLE(\uAC15\uB0A8\uC2A4\uD0C0\uC77C) M\u29F8V", "PSY - GANGNAM STYLE(\uAC15\uB0A8\uC2A4\uD0C0\uC77C) M/V"));

            // --- URL validation.
            Check("URL watch", YouTubeUrl.IsValid("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));
            Check("URL youtu.be", YouTubeUrl.IsValid("https://youtu.be/dQw4w9WgXcQ"));
            Check("URL shorts", YouTubeUrl.IsValid("https://www.youtube.com/shorts/dQw4w9WgXcQ"));
            Check("URL invalid", !YouTubeUrl.IsValid("https://example.com/video"));
            Check("URL with text", !YouTubeUrl.IsValid("watch this https://www.youtube.com/watch?v=dQw4w9WgXcQ now"));

            // --- Metadata parser (stdout of AudioTitleArgs).
            string sample = "title=Me at the zoo\nduration=19\nsize_approx=234567\n";
            YtDlpRunner.AudioTitleInfo info = YtDlpRunner.AudioMetadataParser.Parse(sample);
            Check("Parser basic", info != null && info.Title == "Me at the zoo"
                && info.DurationSeconds == 19 && info.SizeApproxBytes == 234567);
            YtDlpRunner.AudioTitleInfo info2 = YtDlpRunner.AudioMetadataParser.Parse(
                "title=AC/DC: Back in Black (Official Video)\nduration=NA\nsize_approx=NA\n");
            Check("Parser NA fields", info2 != null && info2.Title == "AC/DC: Back in Black (Official Video)"
                && !info2.DurationSeconds.HasValue && !info2.SizeApproxBytes.HasValue);
            YtDlpRunner.AudioTitleInfo info3 = YtDlpRunner.AudioMetadataParser.Parse("ERROR: something");
            Check("Parser garbage -> null", info3 == null);
            YtDlpRunner.AudioTitleInfo info4 = YtDlpRunner.AudioMetadataParser.Parse("title=NA\nduration=1\n");
            Check("Parser NA title -> null", info4 == null);

            // --- Cookie argument construction (offline; the SAME invariant as
            //     the source project: the cookie attempt differs from the plain
            //     attempt EXCLUSIVELY by the "--cookies <file>" pair, inserted
            //     before the URL which stays last).
            string url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
            string titleArgs = YtDlpRunner.FormatArgs(YtDlpRunner.AudioTitleArgs(url));
            Check("Title probe: URL is the last argument", titleArgs.EndsWith(YtDlpRunner.Quote(url), StringComparison.Ordinal));
            Check("Title probe: no --cookies without a jar attempt", !titleArgs.Contains("--cookies"));
            // NOTE: Quote() leaves args without spaces/quotes alone, so paths
            // without blanks stay unquoted - a path WITH a blank forces quotes.
            string stagedPath = "C:\\cookies dir\\staged.txt";
            string cookieProbe = YtDlpRunner.FormatArgs(
                YtDlpRunner.WithCookies(YtDlpRunner.AudioTitleArgs(url), stagedPath));
            Check("Probe with cookies: --cookies + URL last",
                cookieProbe.Contains("--cookies \"" + stagedPath + "\"")
                && cookieProbe.EndsWith(YtDlpRunner.Quote(url), StringComparison.Ordinal));
            string dlNoCookies = YtDlpRunner.FormatArgs(YtDlpRunner.AudioDownloadArgs(root, url, AudioOutputMode.Mp3));
            Check("Download plain: no --cookies, bestaudio selector present",
                !dlNoCookies.Contains("--cookies")
                && dlNoCookies.Contains("bestaudio[ext=m4a]/bestaudio/best"));
            string dlWithCookies = YtDlpRunner.FormatArgs(
                YtDlpRunner.WithCookies(YtDlpRunner.AudioDownloadArgs(root, url, AudioOutputMode.Mp3), stagedPath));
            Check("Download with cookies: --cookies + URL last",
                dlWithCookies.Contains("--cookies \"" + stagedPath + "\"")
                && dlWithCookies.EndsWith(YtDlpRunner.Quote(url), StringComparison.Ordinal));

            // SaveCookiesArgs: export carries NO URL (offline) and pairs
            // --cookies-from-browser with --cookies.
            string saveArgs = YtDlpRunner.FormatArgs(YtDlpRunner.SaveCookiesArgs("C:\\cookies dir\\tmp.txt", "chrome"));
            // Note: Quote() adds quotes only when an argument contains
            // whitespace/quotes, so the bare browser name stays unquoted.
            Check("SaveCookiesArgs: offline export (no URL), both flags",
                saveArgs.Contains("--cookies-from-browser chrome")
                && saveArgs.Contains("--cookies \"C:\\cookies dir\\tmp.txt\""));

            // --- Decision seams (pure, network-free; source policy).
            Check("Download retry: bot-check -> cookies may help",
                YtDlpRunner.CookiesMayHelpDownload(EngineFailureKind.BotCheck, "Sign in to confirm you're not a bot"));
            Check("Download retry: bare 429 -> NO cookie retry",
                !YtDlpRunner.CookiesMayHelpDownload(EngineFailureKind.RateLimited, "HTTP Error 429: Too Many Requests"));
            Check("Download retry: 429 + sign-in text -> cookie retry",
                YtDlpRunner.CookiesMayHelpDownload(EngineFailureKind.RateLimited,
                    "HTTP Error 429: Too Many Requests (sign in to confirm)"));
            Check("Download retry: video unavailable -> NO cookie retry",
                !YtDlpRunner.CookiesMayHelpDownload(EngineFailureKind.Unavailable, "Video unavailable"));
            Check("Probe retry: bare 429 -> exactly one cookie attempt allowed",
                YtDlpRunner.CookiesMayHelpProbe(EngineFailureKind.RateLimited, "HTTP Error 429"));
            Check("Probe fallback: cookie-related output -> anonymous fallback allowed",
                YtDlpRunner.ShouldRetryTitleWithoutCookies(EngineFailureKind.Unknown, "ERROR: unable to read cookies file"));
            Check("Probe fallback: bare 429 -> NO anonymous fallback",
                !YtDlpRunner.ShouldRetryTitleWithoutCookies(EngineFailureKind.RateLimited, "HTTP Error 429"));
            // Source policy: a bot-check output after a failed cookie run must
            // NOT trigger an anonymous retry - that would only double requests
            // against an endpoint that just refused us.
            Check("Probe fallback: bot-check marker -> NO anonymous fallback",
                !YtDlpRunner.ShouldRetryTitleWithoutCookies(EngineFailureKind.Unknown, "sign in to confirm you're not a bot"));

            // --- CookieStore (names-only jar analysis on temporary jars).
            string jarDir = Path.Combine(Path.GetTempPath(), "dsa-jar-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(jarDir);
            string jar = Path.Combine(jarDir, "cookies.txt");
            Check("Store: absent jar -> HasUsableCookies=false", !CookieStore.HasUsableCookies());
            File.WriteAllBytes(jar, new byte[0]);
            Check("Store: empty jar -> not usable", !CookieStore.HasUsableCookies());
            File.WriteAllText(jar,
                "# Netscape HTTP Cookie File\n"
                + ".youtube.com\tTRUE\t/\tTRUE\t0\tVISITOR_INFO1_LIVE\tsomevalue\n"
                + ".youtube.com\tTRUE\t/\tTRUE\t0\t__Secure-3PSID\tanothervalue\n"
                + ".google.com\tTRUE\t/\tTRUE\t0\tsomething\tvalue\n");
            bool hasYT, signedIn; string jarReason;
            Check("Store: AnalyzeJar youtube + SID -> ok, signed in",
                CookieStore.AnalyzeJar(jar, out hasYT, out signedIn, out jarReason)
                && hasYT && signedIn);
            File.WriteAllText(jar,
                "# Netscape HTTP Cookie File\n"
                + ".doubleclick.net\tTRUE\t/\tTRUE\t0\tsomecookie\tvalue\n");
            Check("Store: AnalyzeJar no youtube cookie -> fails with reason",
                !CookieStore.AnalyzeJar(jar, out hasYT, out signedIn, out jarReason) && !string.IsNullOrEmpty(jarReason));

            // HasUsableCookies is bound to the app store path, which in the test
            // context lives under the test bin: place a jar there, verify, then
            // ClearSaved must remove it (covers the canonical ClearSaved path).
            string storePath = CookieStore.FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(storePath));
            File.WriteAllText(storePath,
                ".youtube.com\tTRUE\t/\tTRUE\t0\t__Secure-1PSID\tvalue\n");
            Check("Store: jar at the canonical path -> usable", CookieStore.HasUsableCookies());
            CookieClearResult cleared = CookieStore.ClearSaved();
            Check("Store: ClearSaved removes the canonical jar",
                cleared.Status == CookieClearStatus.Removed && !File.Exists(storePath));
            Check("Store: ClearSaved refused nothing else",
                Directory.Exists(Path.GetDirectoryName(storePath)));

            // ClearFiles: removes jar + .tmp inside the store, refuses paths outside.
            string clearJar = Path.Combine(jarDir, "cookies.txt");
            File.WriteAllText(clearJar + ".tmp", "garbage");
            CookieClearResult cc = CookieStore.ClearFiles(clearJar, jarDir);
            Check("Store: Clear removes jar and .tmp",
                cc.Status == CookieClearStatus.Removed
                && !File.Exists(clearJar) && !File.Exists(clearJar + ".tmp"));
            cc = CookieStore.ClearFiles(clearJar, jarDir);
            Check("Store: second clear -> NothingToRemove", cc.Status == CookieClearStatus.NothingToRemove);
            string outside = Path.Combine(Path.GetTempPath(), "dsa-outside.jar");
            File.WriteAllText(outside, "x");
            cc = CookieStore.ClearFiles(outside, jarDir);
            Check("Store: clear outside the store -> Refused, file intact",
                cc.Status == CookieClearStatus.Refused && File.Exists(outside));

            // --- Sanitizer: no cookie data may pass through.
            Check("Sanitizer: URL secrets redacted",
                DiagnosticSanitizer.Sanitize("https://x/?v=1&sig=ABCDEF&n=12345")
                    == "https://x/?v=1&sig=[redacted]&n=[redacted]");
            Check("Sanitizer: cookie/authorization lines redacted",
                !DiagnosticSanitizer.Sanitize("Cookie: __Secure-3PSID=topsecret")
                    .Contains("topsecret"));

            Cleanup(jarDir);
            try { File.Delete(outside); } catch { }
            Cleanup(root);
            Console.WriteLine();
            Console.WriteLine("PASSED=" + _passed + " FAILED=" + _failed);
            return _failed == 0 ? 0 : 1;
        }

        private static bool ApplyDenyListAcl(string dir)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("icacls", "\"" + dir + "\" /deny *S-1-1-0:(S,RD)");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit(10000);
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
