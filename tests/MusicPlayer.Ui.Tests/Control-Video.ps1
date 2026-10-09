param([Parameter(Mandatory=$true)][int] $AppProcessId,
    [Parameter(Mandatory=$true)][ValidateSet('Inspect','Invoke','Subtitle','Seek','Speed','Close')][string] $Action,
    [string] $Name,[double] $Value)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Video interaction tests require an isolated Windows runner.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$process=Get-Process -Id $AppProcessId
if($process.ProcessName -ne 'MusicPlayer'){throw 'Target is not the launched player.'}
$owned=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$AppProcessId)
$windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$owned)
$window=@($windows | Where-Object {$_.Current.Name -like '*MusicPlayerVideoSmoke*'}) | Select-Object -First 1
if(!$window){throw 'The installed video window did not appear.'}
function Find-Control([string] $Label){
    $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Label)
    $control=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    if(!$control){throw "Video control is missing: $Label"}
    return $control
}
switch($Action){
    Invoke {
        $invokedAt=[DateTime]::UtcNow
        (Find-Control $Name).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        if($Name -eq 'Save a screenshot of the current frame'){
            $screenshots=Join-Path ([Environment]::GetFolderPath('MyPictures')) 'Music Player/Screenshots'
            $snapshot=$null
            for($attempt=0;$attempt -lt 40 -and !$snapshot;$attempt++){
                $snapshot=Get-ChildItem -LiteralPath $screenshots -Filter 'MusicPlayerVideoSmoke-*.png' -ErrorAction SilentlyContinue | Where-Object {$_.LastWriteTimeUtc -ge $invokedAt} | Select-Object -First 1
                if(!$snapshot){Start-Sleep -Milliseconds 200}
            }
            if(!$snapshot -or $snapshot.Length -lt 1000){throw 'The installed video screenshot command did not produce an image.'}
            $bytes=[IO.File]::ReadAllBytes($snapshot.FullName)
            if([BitConverter]::ToString($bytes[0..7]) -ne '89-50-4E-47-0D-0A-1A-0A'){throw 'The video screenshot is not a PNG.'}
            $output=if($env:MUSICPLAYER_TEST_OUTPUT){$env:MUSICPLAYER_TEST_OUTPUT}else{'artifacts/ui-evidence'}
            Copy-Item -LiteralPath $snapshot.FullName -Destination (Join-Path $output 'installed-video.png') -Force
        }
    }
    Seek { (Find-Control 'Video position').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue($Value) }
    Speed {
        $combo=Find-Control 'Playback speed'
        $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 300
        $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name)
        $item=$combo.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
        if(!$item){throw 'Video speed option did not appear.'}
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $selected=$combo.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).GetCurrentSelection()
        if($selected.Count -ne 1 -or $selected[0].Current.Name -ne $Name){throw 'The video speed selection did not persist.'}
    }
    Subtitle {
        (Find-Control 'Subtitles').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 300
        $items=[Collections.Generic.List[object]]::new()
        $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::MenuItem)
        foreach($ownedWindow in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$owned)){
            foreach($item in $ownedWindow.FindAll([Windows.Automation.TreeScope]::Descendants,$condition)){
                if($item.Current.IsEnabled -and $item.Current.Name -match 'Release subtitle|English|eng'){$items.Add($item)}
            }
        }
        if($items.Count -eq 0){throw 'The embedded subtitle did not appear in the video menu.'}
        $items[0].GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Close { $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
}
[pscustomobject]@{action=$Action;videoWindow=$true;name=$Name} | ConvertTo-Json -Compress
