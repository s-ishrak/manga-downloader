@echo off
rem One-click launcher: starts the manga-downloader web UI and opens it in your default browser.
rem Close this window (or press Ctrl+C) to stop it. Downloads in progress resume next time.
rem
rem To rebuild after changing the source, delete the "dist" folder next to this file.

setlocal
title manga-downloader
cd /d "%~dp0"

rem ---- settings: edit these ------------------------------------------------------------------------
set "MODULES=D:\Manga\HDoujin.Downloader-2025.1.6.1-win\modules\lua"
set "OUTPUT=%~dp0downloads"
set "PORT=8780"
rem --browser: when a site shows a Cloudflare check, a popup browser window opens. You pass the check yourself in it; the window
rem closes by itself and the download continues. (Add --paste to paste cookies from your own browser instead.)
rem ---------------------------------------------------------------------------------------------------

set "EXE=%~dp0dist\manga-downloader.exe"

if not exist "%EXE%" (
    echo First run: building manga-downloader, this takes a minute...
    dotnet publish "%~dp0src\MangaDownloader.Cli" -c Release -o "%~dp0dist" --nologo -v q
    if errorlevel 1 goto :build_failed
    echo.
)

rem The popup browser used for Cloudflare checks is a small separate program that has to sit next to the main one.
if not exist "%~dp0dist\manga-downloader-browser.exe" (
    echo Building the popup browser...
    dotnet publish "%~dp0src\MangaDownloader.BrowserWindow" -c Release -o "%~dp0dist" --nologo -v q
    if errorlevel 1 goto :build_failed
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

echo Starting the web UI. Downloads go to: %OUTPUT%
echo.
"%EXE%" serve --modules "%MODULES%" --output "%OUTPUT%" --port %PORT% --browser --open

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
