# fetch it

A Windows 11 app you run **on your PC**. Paste a URL. Save the video or the photos.

YouTube, Shorts, playlists, TikTok, Vimeo, Twitch, Instagram posts and stories, X, Threads, and any other link [yt-dlp](https://github.com/yt-dlp/yt-dlp) or [gallery-dl](https://github.com/mikf/gallery-dl) can read.

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

- **LINK** — the URL. Paste, or it takes a link from the clipboard when you open the window.
- **VIDEO** — Best / 1080p / 720p / Audio. Hidden when the link is photos only.
- **FOLDER** — where files go. Click the name to change it. Default is Downloads.
- **LOGIN** — off / on. Uses your Chrome session for Instagram, X, and Threads stories or private posts. Hidden on YouTube. No password is stored.
- **Fetch** — save everything in the post. Becomes **Stop** while running.

A post with several files goes in a subfolder named after the title.

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
git tag v1.0.0
git push origin v1.0.0
```

That runs `.github/workflows/release.yml`, which tests, publishes a self-contained win-x64 build, fetches tools, and creates a GitHub Release with `fetch-it-v1.0.0-win-x64-setup.exe` and `fetch-it-v1.0.0-win-x64.zip`.

To build the installer locally (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
.\installer\build-installer.ps1
```
