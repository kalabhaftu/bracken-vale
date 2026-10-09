param([Parameter(Mandatory=$true)][string] $Executable)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This smoke check must run on an isolated GitHub runner.' }
$music=Join-Path ([Environment]::GetFolderPath('MyMusic')) 'MusicPlayerSmoke'
New-Item -ItemType Directory -Path $music -Force | Out-Null
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
        $writer.Write([byte[]]::new($size))
    } finally {$writer.Dispose()}
    '[00:00.00]Smoke lyric one' + "`n" + '[00:02.00]Smoke lyric two' | Set-Content -LiteralPath (Join-Path $music "MusicPlayerSmoke-$name.lrc")
}
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS='--remote-debugging-port=9222'
$exe=(Resolve-Path -LiteralPath $Executable).Path
$process=Start-Process -FilePath $exe -PassThru
$env:MUSICPLAYER_TEST_APP_PID=[string]$process.Id
try {
    node (Join-Path $PSScriptRoot 'smoke.mjs')
    if($LASTEXITCODE -ne 0){throw 'Windows UI smoke failed.'}
} finally {
    $process.Refresh()
    if(!$process.HasExited){$null=$process.CloseMainWindow();if(!$process.WaitForExit(20000)){Stop-Process -Id $process.Id -Force}}
}
$process=Start-Process -FilePath $exe -PassThru
$env:MUSICPLAYER_TEST_APP_PID=[string]$process.Id
try {
    node (Join-Path $PSScriptRoot 'smoke.mjs') --restart
    if($LASTEXITCODE -ne 0){throw 'Windows UI restart smoke failed.'}
} finally {
    $process.Refresh()
    if(!$process.HasExited){$null=$process.CloseMainWindow();if(!$process.WaitForExit(20000)){Stop-Process -Id $process.Id -Force}}
    Remove-Item Env:/WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -ErrorAction SilentlyContinue
    Remove-Item Env:/MUSICPLAYER_TEST_APP_PID -ErrorAction SilentlyContinue
}
