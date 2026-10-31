# Manga Downloader — command reference

For installation, features, and a quick start, see the [project README](../README.md). This reference describes advanced CLI options and implementation limitations.

## Layout

```
src/
  MangaDownloader.Net/    IHttpBackend; HttpClient backend (proxy), cookie jar + redirects, Cloudflare/FlareSolverr
  MangaDownloader.Lua/    MoonSharp host: Api/ (userdata types), Runtime/ (script factory, loader, ModuleSession)
  MangaDownloader.Imaging/ Magick.NET: format conversion, unscrambling, image size
  MangaDownloader.Core/   ModuleRepository (load a folder of modules, match URL -> module)
  MangaDownloader.Web/    local control panel (HttpListener + one embedded HTML page)
  MangaDownloader.Cli/    manga-downloader modules list | info | chapters | pages | get | queue | track | serve ...
tests/
  MangaDownloader.Lua.Tests/   API conformance + fixture tests (real upstream modules vs synthetic HTML)
```

Modules are not vendored. Point at a folder of upstream `.lua` files (e.g. a HDoujin install's `modules/lua`):
`--modules DIR`, or `MANGA_DOWNLOADER_MODULES`, or `./modules/lua`.

## Use

```
dotnet run --project src/MangaDownloader.Cli -- modules list --modules "D:\Manga\HDoujin.Downloader-2025.1.6.1-win\modules\lua"
dotnet run --project src/MangaDownloader.Cli -- info  https://hentaifox.com/gallery/12345/
dotnet run --project src/MangaDownloader.Cli -- pages https://hentaifox.com/gallery/12345/
dotnet run --project src/MangaDownloader.Cli -- get https://hentaifox.com/gallery/12345/ --output D:\Manga --format cbz --parallel 6
dotnet test
```

`get` options: `--output DIR`, `--format cbz|zip|folder`, `--parallel N` (6), `--retry N` (4), `--delay MS`,
`--page-format`/`--chapter-format`/`--folder-format` (defaults `%TITLE%_%PAGE%`, `%CHAPTER#% - %TITLE%`, `%TITLE%`),
`--format cbz|zip|pdf|folder`, `--convert webp,avif=jpg:80` (repeatable), `--keep-images`, `--overwrite`. Ctrl+C stops cleanly; rerunning the same command resumes (pages already on disk are kept, an existing archive is skipped).

Formatting strings: `%TOKEN%` (case-insensitive), `%A||B%` fallback, `%ARTIST(2)%` list limit, `%PAGE(000)%` zero padding,
`%IF(a=b,x,y)%` (nestable, also `!=`), `%REGEX(v,pat,grp)%`, `%REPLACE(v,a,b)%`, `%UPPER()%`, `%LOWER()%`, `%TRIM()%`.

## Windows launcher

Install the Windows release, then open **Manga Downloader** from the Start menu. The launcher downloads modules on first use and runs the local interface without a terminal. Close the window to keep downloading; use the tray menu **Exit** to stop. Edit `launcher.json` beside the executable to change the module folder, port, or manual browser challenges. An empty module path uses `%APPDATA%\manga-downloader\modules\lua`.

For source builds, run `Start manga-downloader.cmd` from this directory with the .NET 5 SDK installed. It rebuilds changed source files and launches `dist\Manga Downloader.exe`.

## Queue, history, tracking and the web UI

State lives in one SQLite file (`library.db` in the config folder, WAL mode, versioned schema with automatic upgrades).

```
manga-downloader queue add <url>...        queue list | run | remove <id> | clear
manga-downloader queue import "D:\Manga\HDoujin...\queue.manga" [--map url=6,title=0,progress=11,status=12]
manga-downloader history [--limit 50]      history clear
manga-downloader track add <series-url> [--auto]     track list | check [id] | remove <id>
manga-downloader serve [--port 8780] [--open] [--check-every 60] [--concurrent 3]     # web UI; takes the same download/network options as `get`
```

- **Queue:** URLs are compared ignoring scheme, `www.`, fragment and trailing slash, so the same gallery is never queued twice; a finished/failed/stopped one is queued again instead. Items run one at a time by default; `--concurrent N` (`queue run`, `serve`; 1-8) or the "Downloads at once" box in the web UI lets several download together (the pages of each item download in parallel too; modules are single-threaded Lua, so items on the same module take turns inside it). Progress, errors, outputs and history are written as it goes. If the process dies mid-download, the item goes back to *Queued* on the next start and resumes from the pages already on disk. A list page (tag/search) is one item. Stop an item from the UI and it stays resumable.
- **Progress** is chapter-aware (finished chapters plus the share of the current one), so a 3-chapter series does not hit 100% after its first chapter.
- **`queue.manga` import** (`[HDoujin Download List, V2.1]`): the 43-column layout is not documented and the only file available here was empty, so cells are recognised by content (the gallery URL that is not a favicon, an `n/m` progress cell, a status word, the first free-text cell as title) rather than by position. If your file's columns are read wrongly, give explicit 0-based columns with `--map`. Paused/downloading/waiting entries are queued again, completed ones are kept as completed, and entries already present are skipped. **Verify an import on a real populated file before trusting it.**
- **Tracker:** `track add` records a series' current chapter list as the baseline; `track check` (or `serve --check-every N`) reports chapters that appear later. With `--auto` the series is queued again when new chapters show up; chapters you already have are skipped (existing archives are not re-downloaded). Only series pages with a chapter list can be tracked.
- **Settings:** what the web UI saves goes to `settings.json` in the config folder (only the fields you changed there). `get`, `queue run` and `serve` all read it; a command-line flag overrides it for that run. `Start manga-downloader.cmd` rebuilds `dist` by itself whenever a source file is newer than the last build.
- **Web UI** (`serve`): add/stop/retry/remove downloads, stop and start the whole queue (stopping interrupts the running downloads and puts them back in the queue; starting resumes them), choose how many download at once, a Settings tab (output folder, format, pages at once, retries, delay between pages, keep images, overwrite, how often to check tracked series; used by downloads that start afterwards), history, tracked series with check/auto/download, `queue.manga` import (type a path, pick a file, or drop it on the page). Progress shows pages and percent, and for series the current chapter. If the server stops or restarts, a banner says so and the controls are disabled until it is back. It listens on 127.0.0.1 only. Because a local server that starts downloads must not be controllable by any web page you open, every request needs the per-run access token (printed once in the URL; it becomes an HttpOnly SameSite=Strict cookie), the `Host` header must be 127.0.0.1/localhost with the right port (DNS rebinding), and POSTs must be same-origin `application/json`. Keep the printed URL private. The Import box reads a path on the machine running `serve`.

## Output formats, conversion and unscrambling

- **CBZ** (default, with `ComicInfo.xml`), **ZIP**, **PDF**, or a plain folder. One archive per chapter.
- **PDF:** one image per page; JPEGs are embedded byte-for-byte (no recompression), anything else (WebP, PNG, AVIF, CMYK or transparent images) is first turned into an RGB JPEG. Title/author/tags go into the document info. Page size is the image size at 96 dpi.
- **Conversion rules:** `--convert webp,avif=jpg:80` converts matching pages right after download (formats: jpg, png, webp as targets; any format Magick.NET can read as a source, including AVIF/HEIC/JXL). Transparent areas become white in JPEG; animated images are left alone; a file that fails to convert is kept as downloaded, with a log line. Off by default.
- **Delete after convert:** the pre-conversion original is deleted once its conversion succeeded. With `--keep-images` the image folder is kept after packing and originals are moved to its `_original` sub-folder (never packed). Without it, the image folder is deleted once the archive is written.
- **AfterDownloadPage / unscrambling:** modules that define `AfterDownloadPage()` get `args.Image` (`FilePath`, `Width`, `Height`) and `ImageUnscrambler.New(args.Image)` with `Copy(srcX, srcY, w, h, destX, destY)` and `Save()`; `page.Data` set in `GetPages` is visible. Pages are processed in a `_staging` folder and only moved into place when finished, so a crash never leaves a half-processed page that resume would trust. If the hook errors, that page fails (rather than shipping a scrambled image).

## Network, logins and module updates

Requests go through `Cloudflare solver -> cookie jar + redirects -> HttpClient`. Config lives in `--config DIR` (default `%APPDATA%\manga-downloader`).

```
manga-downloader cookies import "D:\Manga\HDoujin...\config\cookies.json"   # HDoujin's file format, merged into ours; list shows names only
manga-downloader credentials set nhentai.net myuser                              # prompts for the password (or $MANGA_DOWNLOADER_PASSWORD)
manga-downloader get <url> --proxy http://user:pass@host:8080 --flaresolverr http://localhost:8191 --random-ua
manga-downloader modules sync --modules ./modules/lua                            # mirrors the official Lua repo; --source / --repositories to change
```

- **Cookies** persist across runs (session cookies do not). Cookies from redirect hops count for the final response, as modules expect.
- **Login:** if a module defines `Login()` and credentials are stored for its domain, it runs once per session before scraping, and again if the module fails with `LoginRequired`. Passwords are kept in plain text in `credentials.json`; keep that file private. Without a stored login, the error tells you the command to run.
- **Cloudflare:** a challenge (403/503 + `cf-mitigated`/Cloudflare markers) is detected, and cleared once per site; the cookies and user agent are then reused (`cf_clearance` is bound to the user agent). Three ways, none of which automate a browser:
  - `--browser` (**popup, solved by you**): a small browser window (WebView2, the Chromium that ships with Windows 11) opens on the site. You pass the check in it like in any browser. Nothing runs in the page, nothing clicks for you: the window only watches the browser's cookie jar from the outside, and as soon as the clearance cookie is there it closes itself and the tool takes over the cookies and the browser's user agent (`cf_clearance` is tied to it). You can also press the button in the window, or just close it once you are through. The window keeps its own profile (`%APPDATA%\manga-downloader\webview-profile`), so a passed check is remembered and often not asked again. Closing the window before passing, or running out of time (`--browser-timeout`, default 300 s), cancels that item; the tool does not ask again another way.
  - `--browser --paste` (or when the popup cannot run: not Windows, WebView2 or `manga-downloader-browser.exe` missing): the site opens in your own browser instead and you paste the page request (DevTools -> Network -> right-click the first entry -> Copy -> *Copy as cURL*) into the console prompt or the box in the web UI.
  - `--flaresolverr URL`: an external FlareSolverr does it for you (tried first if both are given).
  - `cookies import` / pasting a `cf_clearance` cookie yourself.
  Caveat: the clearance is accepted for requests that look like the browser's. This tool's HTTP client has its own TLS fingerprint, so a strict site may still refuse the cookie; if so the error says the cookies were rejected.
- **Proxy:** http/https only (SOCKS needs .NET 6+).
- **Module sync:** lists the GitHub folder through the contents API, downloads only files whose git blob SHA changed, verifies each download against that SHA, only fetches from `raw.githubusercontent.com`, and only deletes files an earlier sync created. Unauthenticated GitHub API calls are rate limited.
- **Sandbox:** because modules can now come from a remote repository, `dofile`/`loadfile` are removed and `require` accepts plain module names only (no paths).

## Known gaps

- **19 module files are encrypted** (`DoEncryptedString("...")`; the key is inside the obfuscated original exe), plus 2 that `require` them; all 21 are reported as skipped.
- `voyce_me.lua` has a syntax error upstream (flagged `broken` in its own `metadata.json`).
- Not implemented yet: ePUB/RAR/7z/CBW output, a desktop (Avalonia) UI, per-site naming formats, a scheduler beyond `serve --check-every`; `Captcha`, `LZString`; WebDriver/built-in browser fallback and captcha solving; multiple repositories with per-site priority (only the first remote in `repositories.json` is synced).
- Guessed semantics (verify against the real app): `regex()` returns `""` on no match and defaults to the first capture group, `PostData` is cleared after a POST, `GetDomain` takes the last two labels, plain `%PAGE%` pads to at least 3 digits, `%TITLE%` in chapter names is the chapter title (series title if none), a failed page keeps the rest on disk and blocks packing, `GetLanguageName/Id` tables.
- Targets .NET 5 (out of support). A supported-runtime migration is pending; Windows releases bundle the current runtime.
