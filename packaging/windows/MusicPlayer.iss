#ifndef AppVersion
  #error AppVersion must be provided
#endif
#ifndef Architecture
  #error Architecture must be provided
#endif
#ifndef SourceDir
  #error SourceDir must be provided
#endif
#ifndef OutputDir
  #error OutputDir must be provided
#endif

[Setup]
AppId={{8B3371D0-13A1-4410-9E60-A49AD1A5F08C}
AppName=Music Player
AppVersion={#AppVersion}
AppPublisher=Kalabhaftu
DefaultDirName={localappdata}\Programs\Music Player
DefaultGroupName=Music Player
UninstallDisplayIcon={app}\MusicPlayer.exe
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
PrivilegesRequired=lowest
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=MusicPlayer-Setup-{#Architecture}
SetupIconFile={#SourceDir}\Assets\MusicPlayer.ico
Uninstallable=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Music Player"; Filename: "{app}\MusicPlayer.exe"
Name: "{autodesktop}\Music Player"; Filename: "{app}\MusicPlayer.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -ExecutionPolicy Bypass -File ""{app}\Register-MusicPlayer-FileActions.ps1"" -Quiet"; Flags: runhidden waituntilterminated
Filename: "{app}\MusicPlayer.exe"; Description: "Launch Music Player"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -ExecutionPolicy Bypass -File ""{app}\Register-MusicPlayer-FileActions.ps1"" -Unregister -Quiet"; Flags: runhidden waituntilterminated
