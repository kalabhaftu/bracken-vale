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
AppName=Bracken Vale
AppVersion={#AppVersion}
AppPublisher=Kalabhaftu
DefaultDirName={localappdata}\Programs\Bracken Vale
DefaultGroupName=Bracken Vale
UninstallDisplayIcon={app}\BrackenVale.exe
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
PrivilegesRequired=lowest
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=BrackenVale-Setup-{#Architecture}
SetupIconFile={#SourceDir}\Assets\BrackenVale.ico
Uninstallable=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Bracken Vale"; Filename: "{app}\BrackenVale.exe"
Name: "{autodesktop}\Bracken Vale"; Filename: "{app}\BrackenVale.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\BrackenVale.exe"; Description: "Launch Bracken Vale"; Flags: postinstall nowait skipifsilent
