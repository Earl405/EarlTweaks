; Inno Setup script. Build with Inno Setup 6.3+ (https://jrsoftware.org/isinfo.php) after running BUILD-AND-RUN.bat.
[Setup]
AppName=LiveBoost FPS Optimizer
AppVersion=1.5.0
AppPublisher=LiveBoost
DefaultDirName={autopf}\LiveBoost
DefaultGroupName=LiveBoost
OutputDir=installer
OutputBaseFilename=LiveBoost-Setup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=LiveBoost FPS Optimizer
SetupIconFile=Tools\EARL_TWEAKS.ico
UninstallDisplayIcon={app}\LiveBoost.exe

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Files]
Source: "LiveBoost-App\LiveBoost.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\LiveBoost"; Filename: "{app}\LiveBoost.exe"
Name: "{autodesktop}\LiveBoost"; Filename: "{app}\LiveBoost.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LiveBoost.exe"; Description: "Launch LiveBoost"; Flags: nowait postinstall skipifsilent
