# fetch it

A Windows 11 app you run **on your PC**. Paste a URL, **Fetch** a preview, then **Download** the video or the photos.

YouTube, Shorts, playlists, TikTok, Vimeo, Twitch, Instagram, X, Threads, Reddit, OK.ru, and any other link [yt-dlp](https://github.com/yt-dlp/yt-dlp) or [gallery-dl](https://github.com/mikf/gallery-dl) can read.

Not for Netflix, Disney+, Prime Video, or other DRM streams.

## Install

Download **`fetch-it-*-win-x64-setup.exe`** from [Releases](https://github.com/stuckinowhere/fetch-it/releases) and run it.

The installer is per-user (no admin prompt). It includes the .NET runtime, so you do **not** install the .NET SDK. It places the app in `%LocalAppData%\WasdFetchIt` and adds a Start Menu shortcut. Uninstall from **Settings → Apps**.

64-bit Windows 10 (1809 or later) or Windows 11.

A portable zip (`fetch-it-*-win-x64.zip`) is also on each release if you would rather not use Setup.

## Build from source

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (x64).
2. Get this repo onto the machine (clone, or download the ZIP and unzip it).
3. In that folder, either:

**Local publish — a real `FetchIt.exe` with tools next to it**

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\publish-windows.ps1
```

That builds `publish\win-x64\FetchIt.exe`, downloads yt-dlp / gallery-dl / ffmpeg, and launches it.

**Dev loop**

```powershell
.\run-windows.ps1
```

The first fetch downloads the tools into `%LocalAppData%\WasdFetchIt\tools` if they are not already beside the exe.

If PowerShell blocks scripts: `Set-ExecutionPolicy -Scope Process Bypass`.

## What it does

- Paste a link, or it takes one from the clipboard when you open the window.
- **Fetch** — read the link and show a preview. Becomes **Stop** while reading.
- **Download** — save files after Fetch finishes. Shows speed and how much is left. Becomes **Stop** while saving.
- Folder icon — where files go. Defaults to your Windows Downloads folder (including if you moved it). Remembers the last folder you pick.
- Theme icon — light or dark.
- Update icon — check GitHub Releases. On launch the app does this quietly and only prompts when a newer setup exists. **Install** downloads that setup, runs it, and reopens the app.
- Instagram may ask you to sign in in a small window. Chrome can stay open. No password is stored.

A post with several files is saved in the folder you picked, next to each other.

Only download media you have the right to save. Site terms still apply.

## Design

Black `#000000`, white `#FFFFFF`. House brand is **WASD** (hex + WASD keys). Reuse it from [`Brand/`](Brand/README.md). IBM Plex Sans (SIL OFL).

## License

[MIT](LICENSE). IBM Plex Sans is under the [SIL Open Font License](Assets/Fonts/LICENSE.txt).

yt-dlp and gallery-dl are separate tools with their own licenses, downloaded at build or first run. This project is not affiliated with YouTube, Instagram, X, Threads, or any site it can read.

## Cutting a release

After this branch is on `main`:

```powershell
git checkout main
git pull
git tag v1.1.5
git push origin v1.1.5
```

That runs `.github/workflows/release.yml`, which tests, publishes a self-contained win-x64 build, fetches tools, and creates a GitHub Release with `fetch-it-v1.1.5-win-x64-setup.exe` and `fetch-it-v1.1.5-win-x64.zip`.

To build the installer locally (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
.\installer\build-installer.ps1
```
