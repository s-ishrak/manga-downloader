#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
[Setup]
AppId={{87082812-9CF2-4607-BFFD-15D8174F10D7}
AppName=Manga Downloader
AppVersion={#AppVersion}
AppPublisher=s-ishrak
AppPublisherURL=https://github.com/s-ishrak/manga-downloader
AppSupportURL=https://github.com/s-ishrak/manga-downloader/issues
DefaultDirName={localappdata}\Programs\Manga Downloader
DefaultGroupName=Manga Downloader
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\release
OutputBaseFilename=Manga-Downloader-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Manga Downloader.exe
CloseApplications=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "launcher.json"
Source: "..\artifacts\publish\launcher.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\Manga Downloader"; Filename: "{app}\Manga Downloader.exe"
Name: "{autodesktop}\Manga Downloader"; Filename: "{app}\Manga Downloader.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Manga Downloader.exe"; Description: "Launch Manga Downloader"; Flags: nowait postinstall skipifsilent
