@echo off
rem One-click launcher: starts the manga-downloader web UI and opens it in your default browser.
rem Close this window (or press Ctrl+C) to stop it. Downloads in progress resume next time.
rem
rem It rebuilds by itself whenever the source has changed since the last build.

setlocal
title manga-downloader
cd /d "%~dp0"

rem ---- settings: edit these ------------------------------------------------------------------------
set "MODULES=D:\Manga\HDoujin.Downloader-2025.1.6.1-win\modules\lua"
set "PORT=8780"
rem The output folder, format and other download options are set in the web UI (Settings tab) and remembered. The first time,
rem downloads go to the "downloads" folder next to this file. Adding e.g. --output "D:\Manga" to the line that starts the exe
rem below would override the Settings tab on every start.
rem --browser: when a site shows a Cloudflare check, a popup browser window opens. You pass the check yourself in it; the window
rem closes by itself and the download continues. (Add --paste to paste cookies from your own browser instead.)
rem ---------------------------------------------------------------------------------------------------

set "EXE=%~dp0dist\manga-downloader.exe"
set "STAMP=%~dp0dist\.build-stamp"

rem Build when there is no build yet, or when any source file is newer than the last successful build.
set "STALE="
if not exist "%EXE%" set "STALE=1"
if not exist "%~dp0dist\manga-downloader-browser.exe" set "STALE=1"
if not exist "%STAMP%" set "STALE=1"
if not defined STALE (
    powershell -NoProfile -Command "$built = (Get-Item -LiteralPath '%STAMP%').LastWriteTimeUtc; $newer = @(Get-ChildItem -LiteralPath '%~dp0src' -Recurse -File) + @(Get-Item -LiteralPath '%~dp0Directory.Build.props') | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $built } | Select-Object -First 1; if ($newer) { exit 1 }"
    if errorlevel 1 set "STALE=1"
)

if defined STALE (
    if exist "%EXE%" (echo The source has changed: rebuilding manga-downloader...) else (echo First run: building manga-downloader, this takes a minute...)
    dotnet publish "%~dp0src\MangaDownloader.Cli" -c Release -o "%~dp0dist" --nologo -v q
    if errorlevel 1 goto :build_failed
    rem The popup browser used for Cloudflare checks is a small separate program that has to sit next to the main one.
    dotnet publish "%~dp0src\MangaDownloader.BrowserWindow" -c Release -o "%~dp0dist" --nologo -v q
    if errorlevel 1 goto :build_failed
    type nul > "%STAMP%"
    echo.
)

if not exist "%MODULES%\*.lua" (
    echo Could not find the Lua modules in:
    echo   %MODULES%
    echo Open "Start manga-downloader.cmd" in a text editor and fix the MODULES line.
    echo.
    pause
    exit /b 1
)

echo Starting the web UI.
echo.
"%EXE%" serve --modules "%MODULES%" --port %PORT% --browser --open

echo.
echo manga-downloader stopped.
pause
exit /b 0

:build_failed
echo.
echo The build failed. Make sure the .NET SDK is installed ^(run: dotnet --version^), then try again.
echo.
pause
exit /b 1
