param([Parameter(Mandatory=$true)][string] $Executable,[string] $ApplicationId)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This smoke check must run on an isolated GitHub runner.' }
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MusicPlayerShellTest {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string name, string title);
}
'@
& (Join-Path $PSScriptRoot 'Probe-NotificationArea.ps1')
# Tray and thumbnail tests need Explorer in this disposable runner session.
if([MusicPlayerShellTest]::FindWindow('Shell_TrayWnd',$null) -eq [IntPtr]::Zero){
    Start-Process explorer.exe -WindowStyle Hidden
    for($attempt=0;$attempt -lt 60 -and [MusicPlayerShellTest]::FindWindow('Shell_TrayWnd',$null) -eq [IntPtr]::Zero;$attempt++){Start-Sleep -Milliseconds 500}
    if([MusicPlayerShellTest]::FindWindow('Shell_TrayWnd',$null) -eq [IntPtr]::Zero){throw 'The isolated Windows session has no Explorer notification area; tray validation cannot run.'}
}
$music=Join-Path ([Environment]::GetFolderPath('MyMusic')) 'MusicPlayerSmoke'
New-Item -ItemType Directory -Path $music -Force | Out-Null
$videoFixture=Join-Path $env:MUSICPLAYER_MEDIA_FIXTURES 'fixture.mkv'
if(!(Test-Path -LiteralPath $videoFixture)){throw 'The genuine video/subtitle fixture is required for UI validation.'}
Copy-Item -LiteralPath $videoFixture -Destination (Join-Path $music 'MusicPlayerVideoSmoke.mkv') -Force
foreach($name in @('A','B','C','D')) {
    $path=Join-Path $music "MusicPlayerSmoke-$name.wav"
    $writer=[IO.BinaryWriter]::new([IO.File]::Create($path))
    try {
        $size=44100*90*2
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int](36+$size))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16)
        $writer.Write([int16]1); $writer.Write([int16]1); $writer.Write([int]44100); $writer.Write([int]88200)
        $writer.Write([int16]2); $writer.Write([int16]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]$size)
        $samples=[byte[]]::new($size)
        # Distinct valid PCM fixtures must survive default exact-duplicate hiding.
        $samples[0]=[byte][char]$name
        $writer.Write($samples)
    } finally {$writer.Dispose()}
    '[00:00.00]Smoke lyric one' + "`n" + '[00:02.00]Smoke lyric two' | Set-Content -LiteralPath (Join-Path $music "MusicPlayerSmoke-$name.lrc")
}
# Elevated WebView2 150+ ignores environment/HKCU overrides. This app-specific
# machine policy is set only on the disposable runner and removed in finally.
$policy='HKLM:/Software/Policies/Microsoft/Edge/WebView2/AdditionalBrowserArguments'
New-Item -Path $policy -Force | Out-Null
New-ItemProperty -Path $policy -Name 'MusicPlayer.exe' -Value '--remote-debugging-port=9222' -PropertyType String -Force | Out-Null
$exe=(Resolve-Path -LiteralPath $Executable).Path
function Save-Diagnostics {
    $out=Join-Path $PWD 'artifacts/ui-evidence'
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    Get-Process MusicPlayer,msedgewebview2 -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,MainWindowHandle,MainWindowTitle,WorkingSet64,CPU | ConvertTo-Json | Set-Content (Join-Path $out 'startup-processes.json')
    $logs=Join-Path $env:LOCALAPPDATA 'MusicPlayer/Logs'
    if(Test-Path -LiteralPath $logs){Copy-Item -LiteralPath $logs -Destination $out -Recurse -Force}
    Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-10)} -ErrorAction SilentlyContinue | Where-Object {$_.ProviderName -in @('Application Error','.NET Runtime')} | Select-Object TimeCreated,ProviderName,Message | ConvertTo-Json | Set-Content (Join-Path $out 'startup-errors.json')
}
function Start-App {
    if($ApplicationId){return & (Join-Path $PSScriptRoot 'Start-PackagedApp.ps1') -ApplicationId $ApplicationId}
    Start-Process -FilePath $exe -PassThru
}
try {
    foreach($arguments in @(@(),@('--restart'))){
        $process=Start-App
        $env:MUSICPLAYER_TEST_APP_PID=[string]$process.Id
        try {
            node (Join-Path $PSScriptRoot 'smoke.mjs') @arguments
            if($LASTEXITCODE -ne 0){throw 'Windows UI smoke failed.'}
        } finally {
            Save-Diagnostics
            $process.Refresh()
            if(!$process.HasExited){$null=$process.CloseMainWindow();if(!$process.WaitForExit(20000)){Stop-Process -Id $process.Id -Force}}
        }
    }
} finally {
    Remove-ItemProperty -Path $policy -Name 'MusicPlayer.exe' -ErrorAction SilentlyContinue
    Remove-Item Env:/MUSICPLAYER_TEST_APP_PID -ErrorAction SilentlyContinue
}
