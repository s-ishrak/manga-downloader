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

## Supported sites

This snapshot is generated from the community modules available on 31 October 2026. Site availability changes; modules marked adult by upstream appear in the expandable section. Upstream classifications may be incomplete, and listed sites can contain mature content. The app’s **Supported sites** tab shows your installed modules. Encrypted and broken modules are excluded.

- **4chan** — 4chan.org, 4channel.org, boards.4chan.org, boards.4channel.org, yuki.la
- **AnimeAllSar** — animeallstar20.com, www.animeallstar20.com
- **애니툰** — anytoon.co.kr, www.anytoon.co.kr
- **AnyACG** — xbat.la, xbat.si, xbat.app, xbat.io, xbat.tv, xbat.me, xcat.tv, xcat.si, bato.ing, bato.red, bato.si, bato.to, batocomic.org, batotoo.com, battwo.com, comiko.net, dto.to, hto.to, jto.to, kuku.to, mangaseinen.com, mangatensei.com, mangatoto.com, mto.to, okok.to, rawmanga.info, readtoto.org, ruru.to, wto.to, xbato.com, xbato.net, zbato.net, zbato.org
- **AnyACG (V1)**
- **AnyACG (V2)**
- **AnyACG (V4)**
- **Eka's Portal** — aryion.com
- **Asura Scans** — asura.gg, asura.nacm.xyz, asuracomic.net, asuracomics.com, asuracomics.gg, asurascans.com, asuratoon.com, www.asurascans.com
- **包子漫畫** — baozimh.com, www.baozimh.com
- **Bentomanga** — bentomanga.com, www.bentomanga.com
- **BILIBILI COMICS** — bilibilicomics.com, www.bilibilicomics.com
- **BlogTruyen** — blogtruyen.vn, blogtruyenmoi.com
- **CatManga** — catmanga.org
- **CG Cosplay** — cgcosplay.org
- **ChapMangaNelo** — chapmanganelo.org
- **ComicExtra** — comicextra.net, comicextra.org, comixextra.com
- **Comic Fans** — bilibilicomics.net, comicfans.io
- **ComicK** — comick.app, comick.cc, comick.fun, comick.ink, comick.io
- **네이버 만화** — comic.naver.com
- **Comix** — comix.to
- **Constellar Scans** — constellarcomic.com, constellarscans.com
- **Copin Comics** — copincomics.com
- **germa-66.com** — germa-66.com, oremanga.net, skoiiz-manga.com, www.oremanga.net, www.xn--72ca0fgy7cem.com, xn--72ca0fgy7cem.com
- **Crystal Scan** — crystalcomics.com
- **Danbooru** — danbooru.donmai.us
- **Divatoon** — divatoon.com, www.divatoon.com
- **DM5** — dm5.cn, dm5.com, www.dm5.cn, www.dm5.com
- **J18** — doujin.io
- **Dragalia Life** — comic.dragalialost.com
- **EarlyManga** — v1.earlym.org
- **EarlyManga (V2)** — earlym.org, earlymanga.org, v2.earlym.org
- **Endevart** — endevart.com
- **eManga (EndUser)** — kissmangas.com, manganelo.online, manganelo.today, manganelos.com, mangastream.mobi
- **Epik Manga** — epikmanga.com
- **风之动漫** — manhua.fffdm.com
- **Flame Comics** — flamecomics.com, flamecomics.me, flamecomics.xyz, flamescans.org
- **FoOlSlide**
- **reader.deathtollscans.net** — reader.deathtollscans.net, zandynofansub.aishiteru.org
- **FurAffinity** — furaffinity.net, www.furaffinity.net
- **Gelbooru** — gelbooru.com
- **Genkan** — edelgardescans.com, hatigarmscanz.net, hunlight-scans.info, kaguyadex.com, methodscans.com, oneshotscans.com, skscans.com, the-nonames.com, wowescans.co
- **Genkan.io** — genkan.io
- **Glossy Bright**
- **niceoppai.net** — niceoppai.net, www.niceoppai.net
- **GMANGA** — gmanga.me
- **hentai.name** — hentai.name, www.hentai.name
- **Hive Scans** — hivecomic.com, hivescans.com, hivetoon.com, void-scans.com
- **看漫画** — ikanman.com, manhuagui.com, www.manhuagui.com
- **ImageTwist** — imagetwist.com
- **ImageVenue** — imagevenue.com
- **imgbox** — imgbox.com
- **Imperfect Comic** — imperfectcomic.org
- **IsekaiScan** — *, betafox.net, dark-scan.com, disasterscans.com, hunlight.com, isekaiscan.com, ksgroupscans.com, lhtranslation.net, mangasushi.net, mangasushi.org, manhuamanhwa.com, mm-scans.org, novelcrow.com, rackusreads.com, reset-scans.us, reset-scans.xyz, resetscan.com, tcbscans-manga.com, tcbscans.net, theblank.net, www.betafox.net, www.tcbscans.net
- **toonfr.com** — toonfr.com, x-manga.net
- **argoscomic.com** — argoscomic.com, noindexscan.com
- **artessupremas.com** — artessupremas.com, bokugents.com, dominiomarcial.com, hadesnofansub.com, housemangas.com, latan.visorsmr.com, lectorespanol.com, lectormh.com, manhuaespanol.com, mantraz-scan.com, mantrazscan.com, mg.mundodrama.site, mh.cookni.net, mh.twobluescans.com, mhscans.com, panconcola.com, ragnarokscanlation.com, selevertranslation.com, taurusfansub.com, traduccionesamistosas.eyudud.net, visorscamber-scans.com, visortecno.com
- **fcmanga.com** — fcmanga.com, mangasuper.com
- **Jaimini's Box** — jaiminisbox.com
- **Japanread** — japanread.cc, www.japanread.cc
- **Jpmangas** — jpmangas.cc, lelscanvf.cc, lelscanvf.com
- **카카오페이지** — page.kakao.com
- **KissManga** — kissmanga.com, kissmanga.org
- **Komikast** — komikcast.com, komikcast.lol
- **Komiku** — komiku.id
- **KomiRaw** — komiraw.com, manga11.com
- **manhuared.com** — manhuared.com
- **KunManga** — kunmanga.co.uk, www.kunmanga.co.uk
- **LectorManga** — lectormanga.com, followmanga.com
- **LectorXD** — lectorxd.com
- **leercapitulo.co** — leercapitulo.co, www.leercapitulo.co
- **LINE Webtoon** — webtoons.com, www.webtoons.com
- **L漫画** — lmanga.com, www.lmanga.com
- **Luscious** — luscious.net, www.luscious.net, members.luscious.net
- **Madara**
- **1stkissmanga.io** — 1stkissmanga.io, allporncomic.com, kunmanga.com, madaradex.org, manga18fx.com, manga18h.com, manga18sx.com, mangaclash.com, mangadistrict.com, mangadna.com, mangalord.com, mangapl.com, mangaqueen.net, mangastream.cc, mangatx.com, mangatx.to, manhwa-raw.com, muctau.com, paragonscans.com, porncomixonline.net, readfreecomics.com, skymanga.co, teenmanhua.com, toonclash.com, toongod.com, toongod.org, toonily.com, topmanhua.com, vinload.com, webtoon.xyz, www.toongod.com, www.toongod.org
- **3asq.org** — 3asq.org, lekmanga.net, like-manga.net
- **epsilonscan.to** — epsilonscan.to
- **manhwaraw.com** — manhwaraw.com
- **hiper.cool** — hiper.cool, neoxscans.net
- **dragontranslation.net** — dragontranslation.net, emperor-scan.com, emperorscan.org, kns.twobluescans.com, manhwa-latino.com, olympusscanlation.com, seriesemperor.com, zonaemperor.com, submanhwa.net, topcomicporno.com, topcomicporno.net
- **dokimori.com** — dokimori.com, manga-lc.net, manga-post.com
- **araznovel.com** — araznovel.com, mangawow.com, mangawow.net
- **roadsteam.net** — roadsteam.net
- **Madara (Ajax)**
- **niji-translations.com** — niji-translations.com
- **aloalivn.com** — aloalivn.com, astrallibrary.net, earlymanga.net, earlymanga.website, fizmanga.com, gdegenscans.xyz, hentaiwebtoon.org, hmanhwa.com, kissmanga.link, mangaforfree.net, mangaradar.com, manhwaclub.net, manhwatop.com, nightcomic.com, novelmic.com, platinumscans.com, porncomixinfo.net, pornwha.com, x2manga.com
- **desuhentai.net** — desuhentai.net
- **cartoon-th.com** — cartoon-th.com, wwww.cartoon-th.com
- **Madara (Ajax Chap)**
- **manhwahentai.me** — manhwahentai.me, manytoon.club, manytoon.com
- **Madara (No Body Ajax)** — hentai20.com, hiperdex.com, hscans.com, immortalupdates.com, infamous-scans.com, isekaiscanmanga.com, lscomic.com, mangarockteam.com, mangasushi.net, mangasushi.org, manhuaga.com, manhuaplus.com, manhuaus.com, mortalsgroove.com, reset-scans.com, zinmanga.com
- **Maid**
- **maid.my.id** — maid.my.id, www.maid.my.id
- **romance-manga.com** — romance-manga.com
- **MangaBR** — mangabr.net
- **MangaBTT** — mangabtt.com
- **MangaBuddy** — mangabuddy.com, toonily.me
- **MangaDex** — mangadex.org, mangadex.cc, mangadex.com
- **MangaDexTv** — mangadex.tv
- **Mangafast** — mangafast.net
- **MangaFox** — fanfox.net, mangafox.la, mangafox.me
- **MangaFreak** — *.mangafreak.me, *.mangafreak.net, mangafreak.me, mangafreak.net, w12.mangafreak.net, w13.mangafreak.net, w14.mangafreak.net, w15.mangafreak.net, ww1.mangafreak.me
- **MangaHasu** — mangahasu.com, mangahasu.se
- **Mangá Host** — br.mangahost.com, mangahosted.com
- **MangaHub** — mangafox.fun, mangahere.onl, mangahub.io, mangakakalot.fun, mangareader.site
- **MangaJar** — mangajar.com
- **漫画時間** — mangajikan.com, www.mangajikan.com
- **MangaKatana** — mangakatana.com
- **Manga Koma** — mangakoma01.com
- **MangaLib** — hentailib.me, hentailib.org, mangalib.me, mangalib.org, ranobelib.me, v1.hentailib.org
- **MangaLike** — mangalike.ru
- **MangaMint** — mangasail.co
- **MangaMiso** — mangamiso.net
- **Manga Monks** — mangamonks.com
- **Manganelo** — chapmanganato.com, chapmanganato.to, chapmanganelo.com, isekaiscan.site, m.mangabat.com, m.manganelo.com, mangabat.com, mangakakalot.city, mangakakalot.com, mangakakalot.gg, mangakakalot.to, mangakakalot.tv, manganato.com, manganato.gg, manganelo.com, manganelo.tv, mangawk.com, natomanga.com, nelomanga.net, readmangabat.com, readmanganato.com, www.mangakakalot.gg, www.manganato.gg, www.natomanga.com, www.nelomanga.net
- **MangaOwl** — mangaowl.net, *.mangaowls.com, mangaowl.com, mangaowls.com, mostraveller.com, thefashion101.com
- **MangaPark** — v2.mangapark.net
- **MangaPark**
- **Mangapill** — mangapill.com
- **MangaRaw** — manga-raw.club, mcreader.net, www.manga-raw.club, www.mcreader.net
- **Mangareader** — mangareader.net
- **MangaReader** — mangareader.to
- **漫画スイカ** — mangasuika.com, www.mangasuika.com
- **MangaThailand** — mangathailand.com
- **MangaTigre** — mangatigre.com, www.mangatigre.com, www.mangatigre.net
- **Manga Toon** — mangatoon.mobi
- **MangaTown** — mangatown.com, www.mangatown.com
- **MangaWorld** — mangaworld.ac, mangaworld.in, mangaworld.nz, www.mangaworld.ac, www.mangaworld.in, www.mangaworld.nz, www.mangaworld.cx
- **MangaXYZ** — kaliscan.io, mangaxyz.com
- **MangaYeh** — mangayeh.com
- **Manhastro** — manhastro.com
- **漫画DB** — manhuadb.com, www.manhuadb.com
- **ManhuaScan** — kaliscan.com, manhuascan.com, manhuascan.io, manhuascan.me
- **Manhwa Freak** — freakcomic.com, manhwa-freak.com, manhwa-freak.org, manhwafreak.com
- **ManhwaWeb** — manhwaweb.com
- **Manta Comics** — manta.net
- **Meow Scan** — meowscannew.blogspot.com
- **Meraki Scans** — merakiscans.com
- **Mode Scanlator** — modescanlator.com
- **my Manga Reader CMS**
- **hentaishark.com** — hentaishark.com, readcomicsonline.ru, readhent.ai, www.hentaishark.com
- **mangadenizi.com** — mangadenizi.com
- **Nartag** — nartag.com, visortraduccionesamistosas.com
- **Nekopost** — nekopost.net, www.nekopost.net
- **NhatTruyen** — nettruyen.com, nhattruyen.com
- **NiAdd** — niadd.com, nineanime.com, www.niadd.com
- **NineManga** — br.ninemanga.com, de.ninemanga.com, es.ninemanga.com, fr.ninemanga.com, it.ninemanga.com, my.ninemanga.com, ninemanga.com, ru.ninemanga.com, www.ninemanga.com
- **Non Stop Scans** — nonstopscans.com, www.nonstopscans.com
- **Novel Cool** — novelcool.com, www.novelcool.com
- **Olimpo Scanlation** — leerolimpo.com
- **Olympus Scanlation** — leerolymp.com, olympusbiblioteca.com, olympusscans.com, olympusv2.gg, olympusvisor.com, visorolym.com
- **Oredoujin** — oredoujin.com
- **Otaku Sanctuary** — otakusan.net
- **Pixiv** — pixiv.net, www.pixiv.net
- **Qvfamma** — qvfammaonline.blogspot.com
- **Ravenmanga** — ravenmanga.xyz
- **Rawdevart** — rawdevart.art, rawdevart.com, rawsakura.com
- **readm.org** — readm.org
- **ReadManga.Me** — allhen.online, mintmanga.live, mintmanga.one, readmanga.io, readmanga.live, readmanga.me
- **ReadMangaToday** — readmanga.today, readmng.com, www.readmng.com
- **ReadManhwa** — readmanhwa.com
- **ReadOpm** — readsnk.com
- **Reaper Scans** — reaperscans.com
- **Rose Squad Scans** — rosesquadscans.aishiteru.org
- **Safebooru** — *.booru.org, hypnohub.net, realbooru.com, rule34.xxx, safebooru.org, xbooru.com
- **Scylla Scans** — scyllascans.org
- **Seri Manga** — serimanga.com
- **SussyToons** — new.sussytoons.site, oldi.sussytoons.com, sussyscan.com, sussytoons.com, www.sussyscan.com
- **swat manga** — healteer.com
- **Swordflake (Amber)**
- ***** — *, es.ikigaiweb.lat, ikigaimangas.com, ikigaimangas.meope.com, lectorikigai.acamu.net, lectorikigai.efope.com, lectorikigai.erigu.com, visorikigai.imsin.net, visorikigai.meope.com, visorikigai.net, visorikigai.nipase.com, visualikigai.com
- **Tapas** — tapas.io
- **Tappytoon** — tappytoon.com, www.tappytoon.com
- **TaurusManga** — taurusmanga.com
- **TCB Scans** — onepiecechapters.com, tcb-backup.bihar-mirchi.com, tcbonepiecechapters.com, tcbscans.com, tcbscans.me
- **TimelessLeaf** — timelessleaf.com
- **Toomics** — global.toomics.com, toomics.com
- **ToonKor** — tkor.*, tkr0*.com
- **ToonTR Comics** — mangasloth.com
- **Toptoon** — toptoon.com
- **Toptoon** — toptoon.net, www.toptoon.net
- **TOPTOON PLUS** — daycomics.com, toptoonplus.com
- **TruyenQQ** — truyenqq.com
- **TruyenVN** — truyenvn.com
- **TuMangaOnline** — lectortmo.com, visortmo.com, zonatmo.com
- **TurboImageHost** — turboimagehost.com
- **V2PH** — v2ph.com, v2ph.net, www.v2ph.com, www.v2ph.net
- **ViewCartoon** — viewcartoon.com
- **ViewComics** — viewcomics.co
- **Visor TMO Manga** — visormanga.com
- **VK** — vk.com
- **Webnovel** — webnovel.com, www.webnovel.com
- **WebtoonFactory** — webtoonfactory.com, www.webtoonfactory.com
- **Weeb Central** — weebcentral.com
- **늑대닷컴** — wfwf287.com
- **Bluish** — mavimanga.com
- **Flatsome**
- **truyenkinhdien.com** — truyenkinhdien.com
- **Foxiz** — fsicomics.com
- **manga168.com** — manga168.com
- **mangamainac** — readshingekinokyojin.com
- **MangaReader** — *
- **acescans.xyz** — acescans.xyz, alpha-scans.org, anigliscans.com, anigliscans.xyz, arvencomics.com, arvenscans.org, constellarscans.com, edoujin.net, hentai20.io, lunarscan.org, mangagalaxy.me, manhuascan.us, night-scans.com, night-scans.net, nightscans.net, nightscans.org, nightsup.net, nocturnalscans.com, readkomik.com, realmscans.com, realmscans.to, realmscans.xyz, reaper-scans.com, rizzcomic.com, rizzfables.com, suryascans.com, xcalibrscans.com
- **manjanoon.org** — manjanoon.org
- **sushiscan.net** — sushiscan.net
- **159.223.38.69** — 159.223.38.69, kanzenin.info, kiryuu.id, sasangeyou.xyz
- **rawkuma.com** — rawkuma.com
- **sssscanlator.com.br** — sssscanlator.com.br
- **lectorhentai.com** — lectorhentai.com, legionscans.com, miauscan.com, miauscans.com, ragnascan.com, raikiscan.com, shadowmangas.com, shotasekai.club, tecnoscann.com, tresdaos.com
- **108read.com** — 108read.com, turktoon.com
- **108read.com** — 108read.com, doujin-y.com, eye-manga.com, flash-manga.com, god-doujin.com, hippomanga.com, inu-manga.com, joji-manga.com, makimaaaaa.com, manga-moons.net, manga168.net, manga1688.com, mangalami.com, one-manga.com, ped-manga.com, popsmanga.com, reapertrans.com, rom-manga.com, slow-manga.com, spy-manga.com, up-manga.com, webtoonmanga.com, www.doujin-y.com, www.eye-manga.com, www.flash-manga.com, www.inu-manga.com, www.rom-manga.com, www.slow-manga.com, www.up-manga.com, xenon-manga.com
- **MangaStream** — cosmicscans.com, cosmic-scans.com, luminous-scans.com, luminouscomics.org, luminousscans.com, luminousscans.gg, luminousscans.net, lumitoon.com, manhwafreak.site, radiantscans.com
- **miru-manga** — toonsmanga.com
- **Ochanaja** — manga00.com
- **ProWP** — comicsporno.tv
- **PT Magazine** — manga1000.com
- **Toivo Light** — neverland-manga.com, pokemon-manga.com, pokemonmanga.com
- **XoxoComics** — xoxocomics.com
- **YaoiHot** — yaoihot.com
- **YugenMangas** — yugenmangas.com, yugenmangas.lat, yugenmangas.net
- **zBulu** — bulumanga.net, heavenmanga.org, holymanga.net, mytoon.net
- **mangahay.net** — mangahay.net
- **Zerochan** — zerochan.net
- **Zero Scans** — zscans.com

<details>
<summary>Adult / NSFW sites (103)</summary>

- **177漫畫** — 177pic.info
- **18Kami** — 18kami.com
- **8muses** — 8muses.com, 8muses.io, comics.8muses.com
- **999Hentai** — 999hentai.net
- **9hentai** — 9hentai.com, 9hentai.ru, 9hentai.to
- **Ahentai** — ahentai.top, caitlin.top
- **Ahri Gallery** — 04rfcvkubizd0405.top
- **akuma.moe** — akuma.moe
- **아미나** — newtoki*.com, newtoki94.com, newtoki307.com, newtoki308.com
- **ArtUntamed** — artuntamed.com
- **AsmHentai** — asmhentai.com
- **BestComix** — bestporncomix.com, *
- **Cin.wtf** — cin.ac, cin.cin.pw, cin.cx, cin.guru, cin.mom, cin.pw, cin.red, cin.wtf
- **Comics Army** — comicsarmy.com
- **Porn-Comic** — cool-comics.com, cool-manga.com, porn-comic.com, www.cool-manga.com
- **Coolmic** — coolmic.me
- **CrotPedia** — 38.242.194.12, 158.220.106.212, crotpedia.net
- **Doujindesu** — doujindesu.tv, doujindesu.xxx
- **Doujins.com** — doujins.com, www.doujins.com
- **Doujin.sexy** — doujin.sexy
- **Doujin-THAI** — hxani.com
- **FAKKU!** — fakku.net, www.fakku.net
- **GotoFap** — gotofap.org
- **Hanime1.me** — hanime1.me
- **HBrowse** — hbrowse.com
- **Hentai18** — hentai18.net
- **Hentai2.net** — hentai2.net, www2.hentai2.net
- **Hentai20.Online** — hentai20.online, mangahentai.io, manhwahentai.io, manytoon.org
- **Hentai2Read** — hentai2read.com
- **HentaiDexy** — dexyscan.com, hentaidexy.com, hentaidexy.net
- **HentaiEra** — hentaiera.com
- **HentaiForce** — hentaiforce.net
- **HentaiFox** — hentaifox.com
- **HentaiHand** — hentaihand.com, nhentai.com
- **HentaiIdTv** — hentai-id.tv
- **Hentai image** — hentai-img.com
- **HentaiLXX** — hentailxx.com, lxhentai.com
- **HentaiMimi** — hentaimimi.com
- **Load.LA** — box.load.la, hentai.ms, load.la
- **HentaiPaw** — hentaipaw.com
- **HentaiRead** — hentairead.com
- **Hentairead.io** — hentairead.io
- **HentaiRules** — hentairules.net, www.hentairules.net
- **Hentaiser** — hentaiser.com
- **HentaiVN** — ayamehentai.cc, hentaiayame.net, hentaivn.la, hentaivn.moe, hentaivn.net, hentaivn.tv
- **HentaiZap** — hentaizap.com
- **HGameCg** — hgamecg.com, www.hgamecg.com
- **hiyobi.me** — hiyobi.me
- **ImageFap** — imagefap.com, www.imagefap.com
- **IMHentai** — comicporn.xxx, imhentai.com, imhentai.xxx
- **Kemono** — kemono.party, kemono.su
- **Lezhin X** — lezhin.es, lezhinx.com, www.lezhin.es, www.lezhinx.com
- **LolHentai** — lolhentai.net
- **美图鉴赏** — acg.lspimg.com, lspimg.com
- **LXHENTAI** — lxmanga.*, lxmanga.click, lxmanga.ink, lxmanga.life, lxmanga.net, lxmanga.online
- **Manga18.club** — manga18.club
- **Manga18.ME** — manga18.me
- **Манга-тян** — exhentai-dono.me, h-chan.me, hchan.live, henchan.pro, hentai-chan.pro, hentaichan.live, manga-chan.me, y.hentaichan.live, yaoi-chan.me
- **Manhua18** — manhwa18.com
- **Manhwa18** — manhwa18.net
- **Manhwa Manga** — manhwamanga.net
- **ManhwaRead** — manhwaread.com
- **MANHWAS MEN** — manhwas.men
- **M-Hentai** — amanmi.com, m-hentai.net
- **Miku-Doujin** — miku-doujin.com
- **MyAdultComics** — myadultcomics.com
- **MyHentaiGrid** — myhentaicomics.com, myhentaigallery.com, mymangacomics.com
- **MyReadingManga** — myreadingmanga.info
- **Neko Hentai** — neko-hentai.net
- **nhentai** — nhentai.net, 3hentai.net, es.3hentai.net, fra.3hentai.net, hitomila.to, it.3hentai.net, nhentai.to, nhentai.uk, nhentai.xxx, pt.3hentai.net, ru.3hentai.net, simplyhentai.org
- **Nozomi.la** — nozomi.la
- **Nude-Moon!** — nude-moon.com, nude-moon.fun, nude-moon.me, nude-moon.net, nude-moon.org
- **NyaHentai.com** — nyahentai.com
- **NyaHentai** — nyahentai.re
- **OkHentai** — okhentai.net
- **Omega Scans** — omegascans.org
- **Panda Backup** — panda.chaika.moe
- **Pururin** — pururin.io, pururin.me, pururin.to, pururin.us
- **RedGIFs** — redgifs.com
- **Roku Hentai** — rokuhentai.com
- **Rule 34 Comics** — r34comics.com
- **Sex Komix** — sexkomix2.com
- **紳士漫畫** — wnacg.com, www.wnacg.com
- **Simply Hentai** — old.simply-hentai.com, simply-hentai.com, www.simply-hentai.com
- **Sukebe** — sukebe.moe
- **SVSComics** — svscomics.com
- **TheDoujin** — thedoujin.com
- **The Hentai** — *.thehentai.net, en.thehentai.net, thehentai.net
- **TMOHentai** — tmohentai.com
- **Tsumino** — tsumino.com
- **VerMangasPorno** — chochox.com, kingcomix.com, reycomix.com, vcp.xxx, vercomics.xxx, vercomicsporno.com, vermangasporno.com
- **Asap** — comics18.net, comics18.org, comicsparaadultos.com
- **Black Night 24** — toonx.net
- **Blossom Diva** — hentaiporns.net
- **BoxTruyen** — hentai24h.org, hentaime.net
- **ComicsPorno** — hdporncomics.com
- **Herald** — mangalotus.com, yaoidj.com, yaoimangaonline.com
- **MangaStarter** — gntai.net, gntai.xyz
- **XlecX.org** — xlecx.org
- **XXX 3D Comix** — 3dcomixsex.com, porncomix.pro, xxx3dcomix.com
- **XYZ Comics** — xyzcomics.com
- **Yabai!** — yabai.si

- **Tema Fotos** — comicsmanics.com, freeadultcomix.com

</details>

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
