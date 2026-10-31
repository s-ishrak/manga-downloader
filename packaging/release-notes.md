## Manga Downloader 0.1.0

First Windows installer release: a desktop launcher, local web interface, and command-line downloader built around community Lua modules.

### Install

Download **Manga-Downloader-0.1.0-win-x64-Setup.exe** below and run it on Windows 10/11 x64. No administrator access or separate .NET installation is required. Open **Manga Downloader** from the Start menu; first launch downloads community modules and requires internet access. Choose your download folder in **Settings** before adding URLs.

The installer is unsigned. `SHA256SUMS.txt` contains its checksum. WebView2 is used for the desktop window and manual challenge popups; the launcher falls back to your browser if the app window cannot initialize.

### Included

- Persistent queue with resume, retries, concurrent downloads, and history.
- CBZ, ZIP, PDF, and image-folder output; combined chapter archives and CBZ bookmarks.
- Series tracking and optional automatic queuing of new chapters while running.
- Light, dark, and system themes; tray launcher and standalone app window.
- Cookie import, HTTP(S) proxies, module sync, and manual browser challenges.
- First-run module setup without machine-specific paths.
- Corrected ComicInfo XML encoding so CBZ metadata can be read as UTF-8.

Close the window to leave the queue running; use the tray menu **Exit** to stop. Settings and history live in `%APPDATA%\manga-downloader` and are retained on uninstall.

### Known limitations

- This release bundles .NET 5, which is out of support; a supported-runtime migration is pending.
- Encrypted modules are skipped, and individual websites may need module updates or reject requests.
- No automated CAPTCHA solving, EPUB/RAR/7z output, or automatic application updater.
- Credentials are stored in plain text; keep the app's configuration and logs private.

See the [README](https://github.com/s-ishrak/manga-downloader#readme) for setup, command examples, and troubleshooting.
