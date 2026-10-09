param([Parameter(Mandatory=$true)][string] $BundlePath,[Parameter(Mandatory=$true)][string] $Architecture,[Parameter(Mandatory=$true)][string] $PreviousBundlePath)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'MSIX install tests require a disposable Windows runner.'}
if(Get-AppxPackage 'Kalabhaftu.MusicPlayer'){throw 'Expected a clean MSIX installation state.'}
$bundle=(Resolve-Path $BundlePath).Path
$previous=(Resolve-Path $PreviousBundlePath).Path
Add-AppxPackage -Path $previous
$package=Get-AppxPackage 'Kalabhaftu.MusicPlayer'
if($package.Version -ne [version]'0.9.9.0'){throw 'The upgrade fixture did not install its previous package version.'}
$private=Join-Path $env:LOCALAPPDATA "Packages/$($package.PackageFamilyName)/LocalCache/Local/MusicPlayer"
New-Item -ItemType Directory -Path $private -Force | Out-Null
$sentinel=Join-Path $private 'msix-upgrade.txt'
'Persist this previous installation data' | Set-Content $sentinel
Add-AppxPackage -Path $bundle
$package=Get-AppxPackage 'Kalabhaftu.MusicPlayer'
if($package.Version -ne [version]'1.0.0.0' -or !(Test-Path $sentinel)){throw 'MSIX upgrade failed to retain prior data.'}
$exe=Join-Path $package.InstallLocation 'MusicPlayer.exe'
$env:MUSICPLAYER_TEST_OUTPUT='artifacts/ui-evidence/msix'
& (Join-Path $PSScriptRoot 'Run-Smoke.ps1') -Executable $exe -ApplicationId "$($package.PackageFamilyName)!App"
$manifest=Get-AppxPackageManifest $package
$association=$manifest.Package.Applications.Application.Extensions.Extension | Where-Object Category -eq 'windows.fileTypeAssociation'
if(!$association -or '.wav' -notin $association.FileTypeAssociation.SupportedFileTypes.FileType){throw 'MSIX audio file association is missing.'}
& artifacts/Uninstall-MusicPlayer-MSIX.ps1 -Quiet
$data=Join-Path $env:LOCALAPPDATA 'MusicPlayer'
if(Get-AppxPackage 'Kalabhaftu.MusicPlayer'){throw 'MSIX uninstall left the registered package.'}
if(!(Test-Path (Join-Path $data 'library.db')) -or !(Test-Path (Join-Path $data 'msix-upgrade.txt'))){throw 'MSIX retention did not preserve saved library data.'}
Add-AppxPackage -Path $bundle
& artifacts/Uninstall-MusicPlayer-MSIX.ps1 -RemoveUserData -Quiet
if((Get-AppxPackage 'Kalabhaftu.MusicPlayer') -or (Test-Path $data)){throw 'MSIX explicit data removal did not complete.'}
if(!(Test-Path (Join-Path ([Environment]::GetFolderPath('MyMusic')) 'MusicPlayerSmoke/MusicPlayerSmoke-A.wav'))){throw 'MSIX uninstall removed a source music file.'}
Remove-Item Env:/MUSICPLAYER_TEST_OUTPUT -ErrorAction SilentlyContinue
[pscustomobject]@{architecture=$Architecture;installedActivation=$true;upgradeFrom='0.9.9.0';fileAssociation=$true;retainData=$true;removeData=$true;sourceMusicPreserved=$true} | ConvertTo-Json | Set-Content 'artifacts/ui-evidence/msix-results.json'
