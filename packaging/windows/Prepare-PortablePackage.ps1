param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string] $DestinationDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture
)

$ErrorActionPreference = 'Stop'

$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$executable = Join-Path $source 'BrackenVale.exe'
$license = Join-Path $source 'LICENSE'
$notices = Join-Path $source 'ThirdPartyNotices.md'

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published app executable was not found: $executable"
}
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) {
    throw "Published LICENSE was not found: $license"
}
if (-not (Test-Path -LiteralPath $notices -PathType Leaf)) {
    throw "Published third-party notices were not found: $notices"
}
if (Test-Path -LiteralPath $destination) {
    throw "Package destination already exists: $destination"
}

[void][IO.Directory]::CreateDirectory($destination)

$entries = Get-ChildItem -LiteralPath $source -Recurse -Force | Sort-Object { $_.FullName.Length }
foreach ($entry in $entries) {
    $relativePath = [IO.Path]::GetRelativePath($source, $entry.FullName)
    $targetPath = Join-Path $destination $relativePath

    if ($entry.PSIsContainer) {
        [void][IO.Directory]::CreateDirectory($targetPath)
        continue
    }

    if ($entry.Extension -in @('.pdb', '.lib')) {
        continue
    }

    $targetDirectory = Split-Path -Parent $targetPath
    [void][IO.Directory]::CreateDirectory($targetDirectory)
    Copy-Item -LiteralPath $entry.FullName -Destination $targetPath
}

$readme = @'
Music Player __VERSION__ — portable for Windows __ARCHITECTURE__

Launch BrackenVale.exe from this folder.

To add this portable copy to Open with and add Add to Music Player queue / Create Music Player playlist to supported audio-file context menus for your Windows account, run:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Register-MusicPlayer-FileActions.ps1
To remove those entries before moving or deleting this folder, run the same command with -Unregister.
On Windows 11, these classic context-menu actions are under Show more options.

Your library database, settings, artwork cache, and logs are stored in:
%LOCALAPPDATA%\BrackenVale

This package contains the Windows __ARCHITECTURE__ app and its required runtime files.
See LICENSE and ThirdPartyNotices.md for license information.
'@
$readme = $readme.Replace('__VERSION__', $Version).Replace('__ARCHITECTURE__', $Architecture)
[IO.File]::WriteAllText(
    (Join-Path $destination 'Portable-README.txt'),
    $readme,
    [Text.UTF8Encoding]::new($false)
)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Register-MusicPlayer-FileActions.ps1') -Destination (Join-Path $destination 'Register-MusicPlayer-FileActions.ps1')

if (-not (Test-Path -LiteralPath (Join-Path $destination 'BrackenVale.exe') -PathType Leaf)) {
    throw 'Staged package is missing BrackenVale.exe.'
}
if (-not (Test-Path -LiteralPath (Join-Path $destination 'LICENSE') -PathType Leaf)) {
    throw 'Staged package is missing LICENSE.'
}
if (-not (Test-Path -LiteralPath (Join-Path $destination 'ThirdPartyNotices.md') -PathType Leaf)) {
    throw 'Staged package is missing ThirdPartyNotices.md.'
}
if (-not (Test-Path -LiteralPath (Join-Path $destination 'Register-MusicPlayer-FileActions.ps1') -PathType Leaf)) {
    throw 'Staged package is missing the portable file-action registration script.'
}
if (Get-ChildItem -LiteralPath $destination -Recurse -File -Force | Where-Object { $_.Extension -in @('.pdb', '.lib') }) {
    throw 'Staged package unexpectedly contains a PDB or LIB file.'
}
