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

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).
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

The first fetch downloads pinned yt-dlp, ffmpeg, and the standalone gallery-dl.exe into `%LocalAppData%\WasdFetchIt\tools` if they are not already beside the exe. No Python needed. Every binary the app runs (yt-dlp.exe, ffmpeg.exe, ffprobe.exe, gallery-dl.exe) must match the SHA-256 in [`scripts/tool-pins.json`](scripts/tool-pins.json); a folder that fails is ignored.

On launch the app also reads `scripts/tool-pins.json` and `scripts/tool-pins.json.sig` from `main`, so a yt-dlp bump reaches users without a new release. A remote table is accepted only when its ECDSA P-256 signature verifies against the public key compiled into the app (`scripts/tool-pins.pub`), its `version` is higher than the one in use, and every URL sits under the known release paths of yt-dlp, GyanD/codexffmpeg and mikf/gallery-dl. To change pins: edit the JSON, bump `version`, then `node scripts/sign-tool-pins.mjs --key <private-key.pem>` (the key lives outside the repo; CI fails on an unsigned change). `node scripts/new-tool-pins-key.mjs <path>` creates a new key pair.

A log of what the tools said is kept in `%LocalAppData%\WasdFetchIt\logs\fetchit.log` (1 MB, three files) for when a link fails.

If PowerShell blocks scripts: `Set-ExecutionPolicy -Scope Process Bypass`.

## What it does

- Paste a link, drop one onto the window, or let it take one from the clipboard when you open the window.
- English or Spanish, following the Windows display language.
- **Fetch** — read the link and show a preview. Becomes **Stop** while reading.
- **Download** (or Ctrl+Enter) — save files after Fetch finishes. Shows speed and how much is left. Becomes **Stop** while saving. When done, **Open folder** takes you to the files.
- Folder icon — where files go. Defaults to your Windows Downloads folder (including if you moved it). Remembers the last folder you pick.
- Theme icon — light or dark.
- Update icon — check GitHub Releases. On launch the app does this quietly and only prompts when a newer setup exists. **Install** downloads the setup (or the zip for a portable copy), checks it against the release `SHA256SUMS.txt`, runs it, and reopens the app.
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
git tag v1.2.2
git push origin v1.2.2
```

That runs `.github/workflows/release.yml`, which tests, publishes a self-contained win-x64 build, fetches tools, and creates a GitHub Release with `fetch-it-v1.2.2-win-x64-setup.exe`, `fetch-it-v1.2.2-win-x64.zip`, and `SHA256SUMS.txt` (the in-app updater refuses a release without it).

To build the installer locally (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
.\installer\build-installer.ps1
```
