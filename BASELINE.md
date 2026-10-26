# Downloader baseline

This document records the working YouTube audio downloader checkpoint before
Music Search and Audio Trimmer development. Those future features must preserve
the downloader behavior described here.

## Existing downloader behavior

- Accepts a YouTube URL and provides paste from the clipboard through the icon
  embedded in the URL field.
- Offers the current output/container choices: `MP3 (recommended)` and
  `Best available (original)`. The user does not choose an audio bitrate.
- Opens the existing folder picker when the user clicks the `Save to` field and
  persists the selected path in the local `settings.ini` next to the app.
- Provides `Download`, `Cancel`, and `Open Folder` actions.
- Shows download progress and status, and reports validation, duplicate-check,
  download, and filesystem errors through the existing UI handling.
- Checks for a possible duplicate in the selected save folder before starting
  each download; a match is presented to the user for a decision.
- Uses the existing yt-dlp cookie workflow: a saved local cookie jar can be
  used for bounded authentication-related retries, and the app offers its
  existing user-initiated browser-cookie export flow. Diagnostic output is
  sanitized. Cookie jars and diagnostics are local data, not distribution
  files.

## Project and distribution

- `src/` contains the .NET 10 Windows Forms application and its project file.
- `tests/DuplicateCheckTests/` contains the offline console test harness.
- `Assets/` contains the application icon and its source asset/helper.
- `dist/DownloadSearchAudio/` contains the published Windows x64 self-contained
  single-file app plus `yt.exe`, `deno.exe`, `ffmpeg.exe`, and `ffprobe.exe`.
  Those executable files are included in the checkpoint because they are
  required to distribute and run this portable build. User settings and
  generated `data/` content are excluded by `.gitignore`.
- Build the app with `dotnet build src/DownloadSearchAudio.csproj -c Release`.
- Run offline checks with
  `dotnet run --project tests/DuplicateCheckTests/DuplicateCheckTests.csproj -c Release`.
- The project is a local Git repository on `main`; this checkpoint is recorded
  with commit message `Baseline: working YouTube audio downloader`.

## Verification recorded for this checkpoint

- Release build run for this checkpoint: succeeded with 0 warnings and 0 errors.
- Offline test harness run for this checkpoint: 52 passed, 0 failed; S5 was
  skipped because `icacls` was unavailable.
- The latest documented published-EXE GUI verification is the T2.10 check in
  `PROJECT_ROADMAP.md`: the `dist/DownloadSearchAudio/DownloadSearchAudio.exe`
  window was visually checked at the bottom-right, and the layout and spacing
  were confirmed. The Roadmap also records an earlier real download using the
  published EXE. These GUI and live-download checks were not repeated as part
  of preparing this Git checkpoint.
- Cookie export against a real browser session was not exercised; no
  authentication challenge requiring that flow occurred during the documented
  live-download check.
