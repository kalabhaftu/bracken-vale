param(
    [string] $ApplicationName = 'Music Player',
    [switch] $Unregister,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$executable = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'MusicPlayer.exe'))
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Music Player executable was not found: $executable"
}

$extensions = @('.mp3', '.flac', '.wav', '.wave', '.aif', '.aiff', '.m4a', '.m4b', '.mp4', '.aac', '.ogg', '.oga', '.opus', '.wma', '.ape', '.wv', '.tta', '.mpc', '.dsf', '.dff')
$registeredAppsKey = 'HKCU:\Software\RegisteredApplications'
$capabilitiesRelativePath = "Software\BrackenVale\Capabilities\$ApplicationName"
$capabilities = "HKCU:\$capabilitiesRelativePath"
$associationName = "BrackenVale.$($ApplicationName -replace '[^A-Za-z0-9]', '')"
$integrationRoot = "HKCU:\Software\BrackenVale\ShellIntegration\$($ApplicationName -replace '[^A-Za-z0-9]', '')"
$appKey = 'HKCU:\Software\Classes\Applications\MusicPlayer.exe'
$storedExecutable = Join-Path $integrationRoot 'ExecutablePath'

function Set-DefaultValue([string] $Path, [string] $Value) {
    New-Item -Path $Path -Force | Out-Null
    Set-Item -LiteralPath $Path -Value $Value
}

function Set-StringValue([string] $Path, [string] $Name, [string] $Value) {
    New-Item -Path $Path -Force | Out-Null
    New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType String -Force | Out-Null
}

function Remove-OpenWithProgId([string] $Extension, [string] $ProgId) {
    $openWithKey = "HKCU:\Software\Classes\$Extension\OpenWithProgids"
    if (-not (Test-Path -LiteralPath $openWithKey)) { return }

    $key = Get-Item -LiteralPath $openWithKey
    if ($key.GetValueNames() -contains $ProgId) {
        Remove-ItemProperty -LiteralPath $openWithKey -Name $ProgId -ErrorAction SilentlyContinue
    }

    # Keep extension keys owned by Windows or other applications. Only prune this
    # OpenWithProgids subkey if removing our value leaves it entirely empty.
    $key = Get-Item -LiteralPath $openWithKey -ErrorAction SilentlyContinue
    if ($key -and $key.GetValueNames().Count -eq 0 -and $key.GetSubKeyNames().Count -eq 0) {
        Remove-Item -LiteralPath $openWithKey -Force -ErrorAction SilentlyContinue
    }
}

if (-not ('BrackenValeShellAssociations' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class BrackenValeShellAssociations
{
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
'@
}

function Refresh-ShellAssociations {
    # SHCNE_ASSOCCHANGED; refresh Open With and file association caches.
    [BrackenValeShellAssociations]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
}

if ($Unregister) {
    $registeredPath = $null
    if (Test-Path -LiteralPath $storedExecutable) { $registeredPath = (Get-Item -LiteralPath $storedExecutable).GetValue('') }
    if ($registeredPath -and [string]::Equals($registeredPath, $executable, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $integrationRoot -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $capabilities -Recurse -Force -ErrorAction SilentlyContinue
        Remove-ItemProperty -LiteralPath $registeredAppsKey -Name $ApplicationName -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $appKey -Recurse -Force -ErrorAction SilentlyContinue

        foreach ($extension in $extensions) {
            $progId = "$associationName.$($extension.TrimStart('.'))"
            Remove-OpenWithProgId -Extension $extension -ProgId $progId
            Remove-Item -LiteralPath "HKCU:\Software\Classes\$progId" -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath "HKCU:\Software\Classes\SystemFileAssociations\$extension\shell\BrackenValeAddToQueue" -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath "HKCU:\Software\Classes\SystemFileAssociations\$extension\shell\BrackenValeCreatePlaylist" -Recurse -Force -ErrorAction SilentlyContinue
        }
        Refresh-ShellAssociations
        if (-not $Quiet) { Write-Host 'Removed Music Player Open with and context-menu registrations for this install.' }
    }
    elseif (-not $Quiet) {
        Write-Host 'A different Music Player executable is registered; its shell entries were kept.'
    }
    exit 0
}

New-Item -Path $integrationRoot -Force | Out-Null
Set-DefaultValue $storedExecutable $executable
Set-StringValue $registeredAppsKey $ApplicationName $capabilitiesRelativePath
Set-StringValue $capabilities 'ApplicationName' $ApplicationName
Set-StringValue $capabilities 'ApplicationDescription' 'Offline music library and player'
Set-StringValue $capabilities 'ApplicationIcon' "$executable,0"

Set-DefaultValue "$appKey\shell\open\command" ('"' + $executable + '" "%1"')
Set-DefaultValue "$appKey\DefaultIcon" "$executable,0"
foreach ($extension in $extensions) {
    $progId = "$associationName.$($extension.TrimStart('.'))"
    Set-StringValue "$appKey\SupportedTypes" $extension ''
    Set-StringValue "$capabilities\FileAssociations" $extension $progId
    $openWithProgIds = "HKCU:\Software\Classes\$extension\OpenWithProgids"
    Set-StringValue $openWithProgIds $progId ''
    Set-DefaultValue "HKCU:\Software\Classes\$progId" "$ApplicationName audio file"
    Set-DefaultValue "HKCU:\Software\Classes\$progId\DefaultIcon" "$executable,0"
    Set-DefaultValue "HKCU:\Software\Classes\$progId\shell\open\command" ('"' + $executable + '" "%1"')

    foreach ($action in @(
        @{ Key = 'BrackenValeAddToQueue'; Label = 'Add to Music Player queue'; Argument = '--add-to-queue' },
        @{ Key = 'BrackenValeCreatePlaylist'; Label = 'Create Music Player playlist'; Argument = '--create-playlist' }
    )) {
        $verb = "HKCU:\Software\Classes\SystemFileAssociations\$extension\shell\$($action.Key)"
        Set-DefaultValue $verb $action.Label
        Set-StringValue $verb 'Icon' "$executable,0"
        # A Player verb lets Explorer invoke this once for a multi-file selection.
        # The app parses the resulting activation arguments into one ordered queue
        # (or one playlist), instead of launching an action separately per file.
        Set-StringValue $verb 'MultiSelectModel' 'Player'
        Set-DefaultValue "$verb\command" ('"' + $executable + '" ' + $action.Argument + ' "%1"')
    }
}

Refresh-ShellAssociations

if (-not $Quiet) {
    Write-Host 'Registered Music Player under Open with and Windows Default apps.'
    Write-Host 'Windows still requires the user to choose Music Player as the default player.'
    Write-Host 'Added separate Add to Music Player queue and Create Music Player playlist context-menu actions.'
}
