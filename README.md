<div align="center">

# Manga Downloader

**Queue manga. Track new chapters. Build your offline library.**

A Windows desktop launcher and local web interface powered by community Lua scraper modules.

[![Latest release](https://img.shields.io/github/v/release/s-ishrak/manga-downloader)](https://github.com/s-ishrak/manga-downloader/releases/latest)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)
![Formats](https://img.shields.io/badge/formats-CBZ%20%C2%B7%20ZIP%20%C2%B7%20PDF%20%C2%B7%20Images-635BFF)

[**Download for Windows**](https://github.com/s-ishrak/manga-downloader/releases/latest) · [Command reference](manga-downloader/README.md) · [Report an issue](https://github.com/s-ishrak/manga-downloader/issues)

</div>

## Install and start

1. Open [Releases](https://github.com/s-ishrak/manga-downloader/releases/latest) and download **`Manga-Downloader-<version>-win-x64-Setup.exe`** under **Assets**. GitHub's source-code ZIP is for developers.
2. Run the installer. It installs for your Windows account without administrator rights and adds a Start menu shortcut; a desktop shortcut is optional.
3. Open **Manga Downloader**. On first launch, it downloads the community Lua modules from GitHub. Keep your internet connection active; this can take a few minutes.
4. In **Settings**, choose your download folder and output format. Paste a supported series or gallery URL into the queue to start.

**Requirements:** Windows 10/11 x64 and an internet connection. The installer includes the .NET runtime, so no SDK or separate .NET installation is needed. The app window and manual challenge popups use [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). If WebView2 cannot initialize, the launcher opens the interface in your default browser.

The installer is currently unsigned, so Windows may display an unknown-publisher or SmartScreen prompt. Download only from this repository's release assets; `SHA256SUMS.txt` is provided alongside the installer.

## Features

| Feature | What you can do |
| --- | --- |
| Persistent download queue | Add multiple URLs, stop, resume, retry, and keep download history across restarts. |
| Parallel downloads | Run up to eight items at once, with configurable parallel pages, retry counts, and delays. |
| Offline formats | Save CBZ with ComicInfo metadata, ZIP, PDF, or image folders. |
| Multi-chapter collections | Save separate chapter archives or combine a series into one file; combined CBZs include chapter bookmarks. |
| Series tracking | Check for new chapters and optionally queue updated series automatically while the app is running. |
| Image processing | Convert images, keep originals, and run supported modules' image-unscrambling hooks. |
| Desktop convenience | Launch without a terminal, reopen from the tray, and switch between light, dark, and system themes. |
| Community modules | Browse supported sites and update Lua scrapers without rebuilding the app. |
| Network controls | Import cookies, configure HTTP(S) proxies, and handle supported login and manual browser challenges. |
| CLI access | Script downloads, inspect metadata, manage the queue, and import an existing `queue.manga` file. |

Site availability depends on the installed modules and each site's current behavior. The **Supported sites** tab shows what your installation loaded; a listed site is not a guarantee that every URL works.

## Everyday use

- **Settings:** changes apply to downloads that start afterwards. Choose your output directory before adding a large batch.
- **Close versus exit:** closing the app window leaves the background queue running. Use the notification-area icon → **Exit** to stop the app. Interrupted downloads resume when it starts again.
- **New chapters:** add a series to **Tracked series**, then check manually or set the check interval. Automatic checks require the app to remain running.
- **Challenge pages:** complete checks yourself in the popup. Some sites still reject the downloader's requests after a successful browser check.
- **Upgrade:** exit the tray app, then run the new installer over the existing installation. Your library and settings are retained. There is no automatic updater.
- **Uninstall:** use Windows **Installed apps**. Downloads and personal configuration are retained.

## Files and configuration

| Location | Contents |
| --- | --- |
| `%LOCALAPPDATA%\Programs\Manga Downloader` | Default installation directory and command-line executable. |
| `%APPDATA%\manga-downloader` | SQLite queue/history, settings, cookies, credentials, browser profiles, and `launcher.log`. |
| `%APPDATA%\manga-downloader\modules\lua` | Modules downloaded by the launcher. |
| `%APPDATA%\manga-downloader\downloads` | Default launcher download folder until you change it in Settings. |
| `launcher.json` beside the installed app | Optional custom module path, local server port (default `8780`), and browser-challenge setting. A blank module path enables automatic first-run setup. |

Credentials are stored in plain text in `credentials.json`. Keep configuration, logs, and the per-run access-token URL private. The interface listens on loopback only and uses a private token; it is intended for use on your own computer.

## Command-line examples

Open PowerShell in the installation directory. Set the modules path explicitly when using the CLI:

```powershell
$env:MANGA_DOWNLOADER_MODULES = "$env:APPDATA\manga-downloader\modules\lua"

# Refresh modules or list the sites available locally.
.\manga-downloader.exe modules sync
.\manga-downloader.exe modules list

# Replace the example URL with a supported series or gallery URL.
.\manga-downloader.exe get "https://example.com/series/title" --output "$env:USERPROFILE\Downloads\Manga" --format cbz
.\manga-downloader.exe get "https://example.com/series/title" --format cbz --single-file

.\manga-downloader.exe queue list
.\manga-downloader.exe track list
.\manga-downloader.exe history --limit 20
```

See the [full command reference](manga-downloader/README.md) for naming templates, conversions, proxies, cookies, imports, and module compatibility details.

## Troubleshooting and limitations

- **First launch fails:** check your connection and `%APPDATA%\manga-downloader\launcher.log`, then relaunch. GitHub module downloads can be rate limited. Alternatively, set `modules` in `launcher.json` to an existing Lua module folder.
- **Window will not open:** install or repair WebView2, or reopen the interface from the tray to use the browser fallback. If port `8780` is occupied, change `port` in `launcher.json` and restart.
- **A download fails:** refresh modules, check the site's availability, and inspect the queue error. Encrypted modules are skipped; compatibility with every upstream module is not guaranteed.
- **Queue imports:** `queue.manga` columns are inferred. Verify imported entries; use the CLI's `--map` option if columns are misidentified.
- **Current runtime:** version 0.1.0 targets and bundles .NET 5, which is out of support. Migration to a supported runtime is still pending.
- EPUB/RAR/7z output, automated CAPTCHA solving, and a standalone scheduler are not implemented.

## Build from source

The current source targets **.NET 5** and uses Windows Forms for the launcher and browser helper. Build on Windows with the .NET 5 SDK:

```powershell
git clone https://github.com/s-ishrak/manga-downloader.git
cd manga-downloader
dotnet test .\manga-downloader\manga-downloader.sln -c Release
dotnet build .\manga-downloader\manga-downloader.sln -c Release
```

To build the self-contained installer, install [Inno Setup 6](https://jrsoftware.org/isdl.php), then run:

```powershell
.\packaging\build.ps1 -Version 0.1.0 -InnoSetupCompiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
```

The installer and SHA-256 checksum are written to `artifacts\release`. Build scripts package only published application files, not your local library or credentials.

The code is organized under [`manga-downloader/src`](manga-downloader/src): `Core` (downloads, queue, tracking, output), `Lua` (module host), `Net`, `Imaging`, `Web`, `Cli`, `Launcher`, and `BrowserWindow`. Tests live under [`manga-downloader/tests`](manga-downloader/tests).

## Credits

This is an independent implementation of the HDoujin-style Lua module API, using [community scraper modules](https://github.com/HDoujinDownloader/HDoujin-Downloader). It is not affiliated with or endorsed by HDoujin Downloader. Modules are downloaded separately and remain subject to their upstream terms. Use the tool for content you are permitted to download.
