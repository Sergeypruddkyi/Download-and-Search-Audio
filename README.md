# Download and Search Audio

![Download and Search Audio — main window](Assets/Download%20and%20serch%20audio.png)

A portable Windows application for downloading audio from YouTube, searching your music collection, and previewing search results.

## Features

- **Download audio from YouTube** — audio-only using yt-dlp + FFmpeg, Best available quality
- **Search local audio files** — recursive folder search by filename (partial, case-insensitive)
- **Preview search results** — Play and Stop buttons on each result row (NAudio-based)
- **Standard Windows folder picker** — FolderBrowserDialog for save folder and music library

## Installation

Extract the portable ZIP and run `DownloadSearchAudio.exe`. No .NET Runtime installation required.

## First-run behavior

- `settings.ini` is auto-created on first launch if missing
- Last save folder and search folder are remembered between runs
- Cookie jar (`data\cookies\cookies.txt`) is created after browser export

## Build

Self-contained single-file .NET 10 Windows x64 publication in `dist\DownloadSearchAudio\`.