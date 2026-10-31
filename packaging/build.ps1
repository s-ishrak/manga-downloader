param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [string]$InnoSetupCompiler = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo 'artifacts\publish'
$release = Join-Path $repo 'artifacts\release'
if (!(Test-Path -LiteralPath $InnoSetupCompiler)) { throw "Install Inno Setup 6 and pass -InnoSetupCompiler with the path to ISCC.exe." }
if (Test-Path -LiteralPath $publish) {
    $resolved = (Resolve-Path -LiteralPath $publish).Path
    if ($resolved -ne (Join-Path $repo 'artifacts\publish')) { throw 'Unexpected publish directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Force $publish, $release | Out-Null
foreach ($project in @('Cli', 'BrowserWindow', 'Launcher')) {
    dotnet publish (Join-Path $repo "manga-downloader\src\MangaDownloader.$project") -c Release -r win-x64 --self-contained true -o $publish --nologo "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $project." }
}
Copy-Item (Join-Path $repo 'README.md') $publish
Copy-Item (Join-Path $repo 'manga-downloader\README.md') (Join-Path $publish 'COMMAND-REFERENCE.md')
& $InnoSetupCompiler "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'windows.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $release "Manga-Downloader-$Version-win-x64-Setup.exe"
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash.ToLowerInvariant()
"$hash  $(Split-Path $installer -Leaf)" | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Installer: $installer"
