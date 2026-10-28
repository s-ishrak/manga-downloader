# rea-doujin — HDoujin Downloader clone: analysis & plan

Target analyzed: `D:\Manga\HDoujin.Downloader-2025.1.6.1-win` (v2025.1.6.2R0).
Tools: `rea-agents` 6.8.0 (`inspect-managed-artifact`, `inspect-managed-members`) + reading the
315 bundled Lua modules. Raw evidence lives in `re/` (`members.json`, `lua_api.json`, `lua_api.txt`,
`hdoujin_types.txt`).

---

## 1. How HDoujin works

### 1.1 Shape of the program
| Piece | What it is |
|---|---|
| `HDoujin Downloader.exe` | .NET Framework 4.x WinForms app, x86/AnyCPU, IL-only. **Obfuscated** (ConfuserEx-style unicode names on ~3,175 of 3,629 types). Only config classes, the Lua API (`HDoujin.Modules.Lua.UserData.*`) and embedded libs (Newtonsoft.Json, HtmlAgilityPack, BrotliSharp, WebPWrapper) keep real names. |
| `modules/lua/*.lua` (315) | **Site scrapers.** One file per site/engine (Madara, WordPress, FoolSlide, Genkan…). Synced from `github.com/HDoujinDownloader/HDoujin-Downloader/tree/master/modules/lua` (`config/repositories.json`). Plus an "Internal" repo of compiled-in C# modules (e.g. ExHentai, generic). |
| `lib/` | MoonSharp (Lua 5.2 interpreter), Jint + JavaScriptEngineSwitcher (JS eval), HtmlAgilityPack + Saxon-HE/IKVM (XPath), SharpCompress (CBZ/zip/rar), itextsharp (PDF), Magick.NET + libwebp (image conversion), System.Data.SQLite, Selenium WebDriver, libcurl (browser‑impersonating HTTP), BouncyCastle. |
| `bin/browser.exe`, `plugins/flaresolverr.exe` | Cloudflare/challenge solving (headless browser, FlareSolverr). |
| `config.ini` + `config/config.json` + `config/cookies.json` | Legacy INI settings + newer JSON options (network, CF solver, metadata, modules). |
| `queue.manga` | Download queue, `[HDoujin Download List, V2.1]` header then one **43-column TSV** row per entry (title, original title, tags, artist, type, language, url, favicon, domain, module, output dir, progress `0/132`, status `Queued`, …). |

### 1.2 Download pipeline (inferred from module contract + config)
1. **URL in** (paste/clipboard/bookmark/chapter tracker) → match host against every module's
   `module.Domains` (+ `RedirectRules`); fall back to *generic* module if `GenericSupportEnabled`.
2. Engine does `http.Get(url)` and exposes globals `url`, `doc` (string), `dom` (parsed HTML) to the module.
3. `GetInfo()` fills `info.*` (Title, Artist, Tags, Language, PageCount, …). It may call
   `Enqueue(url)` to fan out (search/list pages) and set `info.Ignore = true`.
4. `GetChapters()` (optional, multi-chapter series) fills `chapters` (`Add(url, title)` / ChapterInfo).
5. Per chapter: `GetPages()` fills `pages` with image URLs **or reader-page URLs** (engine then
   resolves the image from that page). Pages may carry `Referer`, `BackupUrls`, `FileExtensionHint`, `Headers`.
6. Per page: optional `BeforeDownloadPage()` (rewrite `page.Url` lazily) → download with rate limits,
   delays, `RetryFailed` → optional `AfterDownloadPage()` (e.g. image unscramble).
7. **Post-process**: name files via format tokens (`%TITLE%_%PAGE%`, `%CHAPTER#% - %TITLE%`), image
   conversion rules (webp/avif→jpg), write metadata (ComicInfo.xml / HDoujin info / JSON), pack to
   **CBZ** / zip / rar / PDF / ePUB, delete folder (`DeleteAfterConversion`).
8. `Login()` is called when credentials exist / module raises `Error.LoginRequired`. Cookies persist
   in `cookies.json`; Cloudflare IUAM handled by solver (curl impersonation → WebDriver → FlareSolverr).

### 1.3 The Lua module contract (the thing the clone must reproduce)
Entry points (count of modules defining them): `Register` 293, `GetInfo` 287, `GetPages` 284,
`GetChapters` 203, `BeforeDownloadPage` 21, `Login` 20, `AfterDownloadPage`/`GetApiEndpoint` 1.

Host objects — **exact members recovered from the binary** (full list in `re/lua_api.txt`):

- **module** (`ModuleUserData`): `Name, Domain, Domains, Language, Adult, Type, Strict, IsGeneric, DeferHttpRequests, Settings, RateLimits, Cookies, Data, GetName()`; `Module.New()` for multi-module files.
- **module.Settings**: `AddCheck(name,bool)`, `AddText(name,str)`, `AddNumeric(name,num)`, `AddChoice(name,default,options).WithMultiSelect()`, `Settings[name]`.
- **module.RateLimits**: `Add(domain, requests, ms)`.
- **info** (`MangaInfoUserData`): Title, AlternativeTitle, OriginalTitle, Artist, Author, Circle, Group, Parody, Characters, Tags, Genres, Language, Type, Category, Series, Magazine, Publisher, Convention, Scanlator, Translator, Uploader, Summary, Description, Status, DateReleased, Published, PageCount, ChapterCount, ReadingDirection, CommunityRating, ThumbnailUrl, Source, Url, Id, Adult, Ignore. (Lists assigned to string props are joined.)
- **chapters** (`ChapterListUserData`): `Add(url,title) | Add(ChapterInfo) | AddRange(...) | Reverse() | Sort() | Count() | [i]`. **ChapterInfo**: Url, Title, Chapter, Volume, Language, Scanlator, ScanlationGroup, Translator, Uploader, Subtitle, Version, ThumbnailUrl.
- **pages** (`PageListUserData`): `Add(str|PageInfo) | AddRange(...) | Remove | Reverse | Sort | Count | Contains | Referer | Headers`. **page/PageInfo**: Url, Referer, BackupUrls, FileNameHint/FilenameHint, FileExtensionHint, DirectoryPath, ThumbnailUrl, Title, ExtractContents, Data.
- **http** (`HttpUserData`): `Get(url)`, `GetResponse(url)`, `Post(url[,data])`, `PostResponse(url[,data])`, `Headers[...]`, `Cookies`, `Referer`, `PostData.Add(k,v)`, `AllowAutoRedirect`, `Http.New()`. **Response**: Body, Document, StatusCode, Headers, Cookies, Url, Data.
- **dom / Dom.New(html[,url])** (`HtmlDocumentUserData`): `SelectValue(xpath)`, `SelectValues`, `SelectElement(s)`, `SelectNode(s)`, `GetElementById/ByClassName/ByTagName`, `Title, Body, Head, Links, Images, Scripts, Forms, Url, BaseUri`. **Node**: same selectors + `GetAttribute, HasAttribute, InnerText, InnerHtml, OuterHtml, TextContent, TagName, Id, ClassName, ClassList, Children, ChildNodes, ParentNode, Next/PreviousSibling, First/LastChild, [i], [attr]`.
- **Json** (`Json.New(str|table)`): `SelectValue/SelectValues/SelectToken/SelectTokens/SelectNode(s)` (JSONPath), `[key]`, `[i]`, `Keys`, `Key`, `Value`, `Count()`, iteration, `Escape`.
- **JavaScript** (`JavaScript.New([src])`): `Execute(code)`, `GetObject(name)`, `JavaScript.Deobfuscate(src)`.
- **global**: `GetSetting/SetSetting`, `SetCookie(domain,name,value)`, `SetCookies(list)`.
- **Url** (`Url.New(href[,base])`, WHATWG-like fields), **Regex** (`IsMatch, Match, Matches, Replace, Escape`), **Hash** (`MD5, Sha256`), **Image** (`Width, Height`), **LZString**, **Paginator**, **Captcha**, **ImageUnscrambler**, **List.New()**, **Error** codes (`LoginRequired, LoginFailed, CaptchaRequired, DomainNotSupported`).
- **Free functions** (by usage): `isempty`, `toboolean`, `RegexReplace`, `FormatString`, `Enqueue`, `Fail`, `FailIfLoginRequired`, `GetRoot`, `GetRooted`, `GetHost`, `GetDomain`, `GetParameter`, `SetParameter`, `StripParameters`, `DecodeBase64`, `Log`, plus `username`/`password` globals in `Login`.
- **String extensions** (`("s"):x()`): `regex(pat[,group])`, `regexmany`, `contains`, `trim([chars])`, `after`, `afterlast`, `before`, `beforelast`, `between`, `replace`, `split`, `startswith`, `endswith`, `title`, `lower`/`upper`, `first`/`last`.

**Host semantics modules rely on (critical for compatibility):**
- Methods work with **both** `obj.Method(x)` and `obj:Method(x)` (MoonSharp behaviour).
- .NET lists are **0-indexed** (`list[0]`) and directly iterable: `for x in dom.SelectValues(...) do` (207 occurrences).
- `tostring(userdata)` yields text/HTML; `isempty()` accepts nil, "", empty lists.
- Regex syntax is **.NET** (`(?i)`, named groups, lookbehind). XPath is **1.0** (+ a few `matches()/replace()`-free usages; HtmlAgilityPack semantics on malformed HTML).

### 1.4 Gaps filled from public sources
The obfuscated exe hides the engine, but most of it is published elsewhere:

| Source | What it gives us |
|---|---|
| **github.com/HDoujinDownloader/HDoujinDownloader** (last commit 2026‑06‑24) | Only `modules/lua/` (313 files). **Identical** to the local install except local also has `LezhinUS.lua` + `metadata.json`. Not outdated for modules; it simply contains no engine code. Git history (2020→) shows how the module API evolved. |
| **…/HDoujinDownloader.wiki** (2026‑04‑12) | User docs that specify engine behaviour: **formatting strings** (full token list, `%A\|\|B%` fallbacks, `%TOKEN(n)%` list limits, `%TOKEN(000)%` padding, nested `%IF(a=b,x,y)%`, `%REGEX(v,pat,grp)%`, `%REPLACE(v,a,b)%`, case-insensitive tokens); **module repositories** (priority order, first repo wins per site, sync on startup, "Internal" = compiled-in modules); **tag rules** (LANraragi syntax: `-tag`, `-ns:*`, `~ns`, `a -> b`, `ns:* -> ns2:*`); **Cloudflare** flow (IUAM vs Captcha detection → FlareSolverr → built-in browser → manual `cf_clearance` + UA); cookies editor; bearer tokens via Site preferences; "Add from list" URL range generator `?page=(1-10)`; output formats ZIP/CBZ, RAR/CBR, PDF, CBW, 7Z. |
| **github.com/gsemac/Gsemac.Common** (MIT, on NuGet) — the author's own libraries, bundled into the exe (`gsemac.net` strings) | The engine's building blocks: `Gsemac.Net` (HTTP, cookies, Brotli, DNS, header ordering per browser), `Gsemac.Net.Curl` (libcurl bindings), `Gsemac.Net.GitHub` (**module repo sync**), `Gsemac.Net.JavaScript` (**`JavaScript.Deobfuscate`**: Dean Edwards packer, SoJson v4, escape sequences, bracketed access), `Gsemac.Net.WebBrowsers` (read/decrypt Chrome & Firefox cookies), `Gsemac.Net.WebDrivers` (Selenium driver auto-update), `Gsemac.IO.Compression` (zip/7z/WinRAR archives), `Gsemac.Drawing` (+ WebP/ImageMagick: image conversion, resize, trim), `Gsemac.Text` (**the Lua string extensions**: `After/AfterLast/Before/BeforeLast/Between` — return the input unchanged when the substring is not found), `Gsemac.Text.Ini` (`config.ini`), `Gsemac.Reflection.Plugins` (plugin loading). |
| **github.com/gsemac/Gsemac.Net.Cloudflare** (NuGet `Gsemac.Net.Cloudflare*`; **no license file in repo**) | Challenge detection (`ProtectionType`), solution cache, FlareSolverr service/updater, Cloudscraper, Selenium solvers. Use as a reference or NuGet dependency only, not copied source, until the license is clear. |
| **github.com/gsemac/curl-impersonate-win** | The `libcurl-x64.dll` in `lib/` (Chrome/Safari TLS impersonation). |
| **github.com/gsemac/lz-string-vbnet** (NuGet `LZStringVBNet`) | Backs `LZString.decompressFromBase64` in Lua. |
| `modules/lua/metadata.json` (local only) | Per-module cache written by sync: display name, `lastUpdated` + `notes` (= last commit date/message for that file), declared settings, `broken` flag. |

**Still not recoverable (needs black-box testing against the real app):** the compiled-in "Internal"
modules (e.g. ExHentai; its domains are in encrypted strings), the generic fallback scraper, the
`queue.manga` column meanings beyond the obvious ones, `IsGeneric`/`Strict`/`DeferHttpRequests`, and
`ImageUnscrambler`/`Paginator`/`Captcha` semantics.

---

## 2. Clone plan

### 2.1 Recommended stack: **C# / .NET 8**, cross-platform core + CLI first
Reason: the clone's value is running the **existing 315 Lua modules unchanged**. The original sits on
MoonSharp + HtmlAgilityPack + .NET Regex + Jint; using the same libraries gives identical dot/colon
call semantics, 0-indexed iterable userdata, XPath quirks and regex dialect for free. Re-implementing those
in Python (`lupa` + `lxml` + `regex`) is doable but every one of the semantics above becomes a shim and a
source of per-module breakage.

Second reason: the author's own MIT libraries (`Gsemac.*` on NuGet, §1.4) cover HTTP/curl, cookies,
JS deobfuscation, archives, images, INI and GitHub sync. Using them reproduces the original's helper
behaviour exactly instead of guessing.

| Concern | Library |
|---|---|
| Shared helpers | `Gsemac.Text`, `Gsemac.Net`, `Gsemac.Net.JavaScript`, `Gsemac.IO.Compression`, `Gsemac.Net.GitHub`, `Gsemac.Text.Ini` (MIT) |
| Lua runtime | MoonSharp 2.x |
| HTML/XPath | HtmlAgilityPack (+ AngleSharp optional for CSS) |
| JSON/JSONPath | Newtonsoft.Json |
| JS eval | Jint |
| HTTP | `HttpClient` (+ optional curl-impersonate via CurlThin / `curl_cffi` sidecar) |
| Cloudflare | FlareSolverr HTTP API (external), later Playwright/Selenium |
| Images | SixLabors.ImageSharp (+ WebP/AVIF), Magick.NET optional |
| Archives/PDF | `System.IO.Compression` (CBZ), PdfSharp / QuestPDF; ePUB later |
| Storage | SQLite (Microsoft.Data.Sqlite) for queue/history; keep importer for `queue.manga` |
| UI (later) | Avalonia (cross-platform) or a local web UI |

### 2.2 Project layout
```
src/
  ReaDoujin.Core/        download engine, queue, naming, post-processing
  ReaDoujin.Lua/         MoonSharp host: UserData types mirroring §1.3, string ext, globals
  ReaDoujin.Net/         http client, cookies, rate limiter, CF solver adapters
  ReaDoujin.Cli/         `rea-doujin get <url>`, `info <url>`, `queue …`, `modules sync`
  ReaDoujin.Gui/         (phase 5)
modules/lua/             synced from upstream repo (not vendored in git)
tests/
  ReaDoujin.Lua.Tests/   API conformance tests per UserData type
  ModuleSmoke/           recorded HTML fixtures → expected info/pages per module
```

### 2.3 Phases & verifiable exit criteria
**Phase 0 – Harness (½ day)**
- Solution skeleton, CLI that loads a `.lua` file and calls `Register()`.
- ✅ `rea-doujin modules list` prints Name/Domains for all 315 modules with 0 load errors.

**Phase 1 – Lua host API (core of the work)**
- Implement UserData types from §1.3 with the exact member names; register `string` extensions and free functions; support `obj.M()` and `obj:M()`.
- Fixture-driven tests: save HTML for ~10 representative sites (HentaiFox, NHentai, Madara, MangaDex API/JSON, WordPress, FoolSlide, one JS-heavy, one Login module).
- ✅ For those fixtures `GetInfo/GetChapters/GetPages` produce the same title/page counts as HDoujin (compare against what HDoujin shows for the same URL).

**Phase 2 – Download engine**
- Queue → resolve module → info → chapters → pages → `BeforeDownloadPage` → parallel download (`DownloadsMax`, `PageDelay`, `RetryFailed`, `module.RateLimits`, Referer/headers/BackupUrls) → `AfterDownloadPage`.
- Resolve reader-page URLs to images (generic extraction: largest `<img>` / og:image) when `pages` holds HTML pages.
- Formatting-string engine as specified by the wiki: tokens (`%TITLE% %PAGE% %CHAPTER#% %ARTIST% …`), `||` fallback, `(n)` limit, `(000)` padding, nested `%IF%`, `%REGEX%`, `%REPLACE%`. Write a unit test for each wiki example. Also `FixLongPaths`, special-char replacement, skip-existing.
- Tag rules (LANraragi syntax) applied before metadata/folder naming.
- ✅ `rea-doujin get <nhentai/hentaifox url>` produces a folder with N correctly ordered, correctly-extensioned images; interrupted download resumes.

**Phase 3 – Post-processing & metadata**
- CBZ/ZIP (default), PDF; image conversion rules (webp/avif→jpg q80); ComicInfo.xml + JSON info file; delete-after-convert.
- ✅ Output CBZ opens in a reader (e.g. KOReader/CDisplayEx) with correct ComicInfo title/artist/tags.

**Phase 4 – Network hardening**
- Persistent cookie jar (import `config/cookies.json`), `Login()` flow with stored creds, `global.SetCookie(s)`, FlareSolverr adapter, randomized UA, Brotli, proxy.
- Module repo sync from upstream GitHub (`repositories.json` semantics).
- ✅ A CF-protected module and a login-required module succeed end-to-end.

**Phase 5 – Queue persistence & UI**
- SQLite queue + `queue.manga` V2.1 importer (43-col TSV); history; chapter tracker (check series for new chapters); scheduler.
- GUI (Avalonia) mirroring main grid columns: Title, Chapters, Pages, Status, Progress, File Size.
- ✅ Import the existing `D:\Manga\…\queue.manga` and resume all entries.

**Out of scope initially:** ePUB, rar via WinRAR, WebDriver-driven modules, captcha solving, licensing/activation, auto-updater.

### 2.4 Risks / open questions
- **Obfuscated engine**: most helpers are now covered by `Gsemac.*` (§1.4). Still unknown: internal compiled modules
  (ExHentai, generic scraper, image unscramblers) and the exact semantics of `GetRooted`, `FormatString` and `regex`
  group defaults. Approach: infer from module usage + black-box compare against the real app on the same URLs.
- **Licensing**: `Gsemac.Common` is MIT. `Gsemac.Net.Cloudflare` has no license in its repo, so check the NuGet license before redistributing it. (`rea`'s
  member decode hit its 64 MB capacity on the full assembly; per-type `--type` queries work.)
- `IsGeneric` / `Strict` / `DeferHttpRequests` behaviour must be inferred empirically.
- Modules updated upstream may use API added after 2025.1.6 — conformance tests should run against the synced repo.
- Site ToS / legality of downloading content is the user's responsibility; the clone ships no site credentials.

### 2.5 Next step
Phase 0 + start Phase 1: scaffold the .NET solution, load all 315 modules, implement `module`, `info`,
`dom`, `http`, `pages`, string extensions, and get **HentaiFox** + **NHentai** passing from fixtures.
