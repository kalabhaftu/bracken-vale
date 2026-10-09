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
DefaultDirName={autopf}\Music Player
DefaultGroupName=Music Player
UninstallDisplayIcon={app}\MusicPlayer.exe
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
PrivilegesRequired=lowest
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=MusicPlayer-Setup-{#Architecture}
SetupIconFile={#SourceDir}\Assets\MusicPlayer.ico
Uninstallable=yes
#ifdef SignedBuild
SignedUninstaller=yes
SignTool=musicplayer
#endif

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "MicrosoftEdgeWebView2Setup.exe,Portable-README.txt,Uninstall-MusicPlayer.ps1"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\MicrosoftEdgeWebView2Setup.exe"; Flags: dontcopy

; Remove obsolete app binaries/resources on upgrade. User data lives outside {app}.
[InstallDelete]
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{autoprograms}\Music Player"; Filename: "{app}\MusicPlayer.exe"
Name: "{autodesktop}\Music Player"; Filename: "{app}\MusicPlayer.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -ExecutionPolicy Bypass -File ""{app}\Register-MusicPlayer-FileActions.ps1"" -Quiet"; Flags: runhidden waituntilterminated
Filename: "{app}\MusicPlayer.exe"; Description: "Launch Music Player"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -ExecutionPolicy Bypass -File ""{app}\Register-MusicPlayer-FileActions.ps1"" -Unregister -Quiet"; Flags: runhidden waituntilterminated

[Code]
var
  RemoveMusicPlayerData: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if UninstallSilent then
      RemoveMusicPlayerData := ExpandConstant('{param:REMOVEUSERDATA|0}') = '1'
    else
      RemoveMusicPlayerData := MsgBox(
        'Also delete Music Player''s local library index, playlists, favorites, settings, artwork cache, and logs? This does not delete your music files. Choose No to keep your library data if you may reinstall.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end
  else if (CurUninstallStep = usPostUninstall) and RemoveMusicPlayerData then
  begin
    { Check parameters are evaluated by Setup, not by the uninstaller. }
    if not DelTree(ExpandConstant('{localappdata}\MusicPlayer'), True, True, True) then
      MsgBox('Some saved Music Player data could not be removed. Close Music Player and remove its folder in Local AppData manually.', mbError, MB_OK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  ExtractTemporaryFile('MicrosoftEdgeWebView2Setup.exe');
  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebView2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Music Player requires the Microsoft Edge WebView2 Runtime. The installer could not start its Microsoft-signed runtime installer.';
    exit;
  end;
  if (ResultCode = 3010) or (ResultCode = 1641) then
  begin
    NeedsRestart := True;
    exit;
  end;
  if ResultCode <> 0 then
    Result := 'The Microsoft Edge WebView2 Runtime could not be installed. Connect to the internet and try Setup again.';
end;
