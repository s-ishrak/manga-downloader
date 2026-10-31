@echo off
rem One-click launcher: starts the manga-downloader web UI and opens it in a standalone window.
rem Use the notification-area menu Exit to stop the app.
rem
rem It rebuilds by itself whenever the source has changed since the last build.

setlocal
title manga-downloader
cd /d "%~dp0"

set "EXE=%~dp0dist\manga-downloader.exe"
set "STAMP=%~dp0dist\.build-stamp"

rem Build when there is no build yet, or when any source file is newer than the last successful build.
set "STALE="
if not exist "%EXE%" set "STALE=1"
if not exist "%~dp0dist\manga-downloader-browser.exe" set "STALE=1"
if not exist "%~dp0dist\Manga Downloader.exe" set "STALE=1"
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
    dotnet publish "%~dp0src\MangaDownloader.Launcher" -c Release -o "%~dp0dist" --nologo -v q
    if errorlevel 1 goto :build_failed
    type nul > "%STAMP%"
    echo.
)

start "" "%~dp0dist\Manga Downloader.exe"
exit /b 0

:build_failed
echo.
echo The build failed. Make sure the .NET SDK is installed ^(run: dotnet --version^), then try again.
echo.
pause
exit /b 1
