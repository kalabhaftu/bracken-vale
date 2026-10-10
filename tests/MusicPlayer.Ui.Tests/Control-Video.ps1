param([Parameter(Mandatory=$true)][int] $AppProcessId,
    [Parameter(Mandatory=$true)][ValidateSet('Inspect','Invoke','Subtitle','Seek','Speed','Escape','Close')][string] $Action,
    [string] $Name,[double] $Value)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Video interaction tests require an isolated Windows runner.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VideoWindowBounds {
    [StructLayout(LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo {public int Size;public Rect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [StructLayout(LayoutKind.Explicit,Size=40)] public struct Input {
        [FieldOffset(0)] public uint Type;[FieldOffset(8)] public ushort Key;[FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,System.Text.StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll")] static extern uint SendInput(uint count,Input[] inputs,int size);
    public static void Escape(IntPtr window) {
        SetForegroundWindow(window);
        // Foreground activation across input queues is asynchronous. Microsoft's
        // documented automation pattern waits for WM_NULL before checking it.
        IntPtr result;
        if(SendMessageTimeout(window,0,IntPtr.Zero,IntPtr.Zero,2,5000,out result)==IntPtr.Zero)
            throw new InvalidOperationException("The video window did not process foreground activation within five seconds.");
        if(GetForegroundWindow()!=window){
            var title=new System.Text.StringBuilder(256);GetWindowText(GetForegroundWindow(),title,title.Capacity);
            throw new InvalidOperationException("The video window did not obtain foreground focus for Escape; foreground window: "+title+".");
        }
        var inputs=new[] {new Input {Type=1,Key=0x1B},new Input {Type=1,Key=0x1B,Flags=2}};
        if(SendInput(2,inputs,40)!=2)throw new InvalidOperationException("Windows rejected the video Escape key input.");
    }
    public static bool Fullscreen(IntPtr window) {
        Rect rect;var info=new MonitorInfo {Size=Marshal.SizeOf(typeof(MonitorInfo))};
        if(!GetWindowRect(window,out rect)||!GetMonitorInfo(MonitorFromWindow(window,2),ref info))throw new InvalidOperationException("Could not inspect the video window bounds.");
        return Math.Abs(rect.Left-info.Monitor.Left)<=2&&Math.Abs(rect.Top-info.Monitor.Top)<=2&&Math.Abs(rect.Right-info.Monitor.Right)<=2&&Math.Abs(rect.Bottom-info.Monitor.Bottom)<=2;
    }
}
'@
$process=Get-Process -Id $AppProcessId
if($process.ProcessName -ne 'MusicPlayer'){throw 'Target is not the launched player.'}
$owned=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$AppProcessId)
$windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$owned)
$window=@($windows | Where-Object {$_.Current.Name -like '*MusicPlayerVideoSmoke*'}) | Select-Object -First 1
if(!$window){throw 'The installed video window did not appear.'}
function Find-Control([string] $Label){
    $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Label)
    $control=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    if(!$control -and ($Label -match 'full screen' -or $Label -in @('Play','Pause'))){
        $id=if($Label -match 'full screen'){'VideoFullscreen'}else{'VideoPlayPause'}
        $byId=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
        $control=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$byId)
    }
    if(!$control){throw "Video control is missing: $Label"}
    return $control
}
switch($Action){
    Invoke {
        $invokedAt=[DateTime]::UtcNow
        (Find-Control $Name).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        if($Name -match 'full screen'){
            $expected=$Name.StartsWith('Enter')
            $handle=[IntPtr]$window.Current.NativeWindowHandle
            for($attempt=0;$attempt -lt 30 -and [VideoWindowBounds]::Fullscreen($handle) -ne $expected;$attempt++){Start-Sleep -Milliseconds 200}
            if([VideoWindowBounds]::Fullscreen($handle) -ne $expected){throw 'Video fullscreen presenter did not change the actual window bounds.'}
        }
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
        $selected=$combo.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
        if($selected.Count -ne 1 -or $selected[0].Current.Name -ne $Name){throw 'The video speed selection did not persist.'}
    }
    Subtitle {
        (Find-Control 'Subtitles').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $items=[Collections.Generic.List[object]]::new()
        $menuType=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::MenuItem)
        $condition=[Windows.Automation.AndCondition]::new($owned,$menuType)
        for($attempt=0;$attempt -lt 30 -and $items.Count -eq 0;$attempt++){
            foreach($item in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,$condition)){
                if($item.Current.IsEnabled -and $item.Current.Name -match 'Release subtitle|English|eng'){$items.Add($item)}
            }
            if($items.Count -eq 0){Start-Sleep -Milliseconds 200}
        }
        if($items.Count -eq 0){throw 'The embedded subtitle did not appear in the video menu.'}
        $items[0].GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Escape {
        $handle=[IntPtr]$window.Current.NativeWindowHandle
        [VideoWindowBounds]::Escape($handle)
        for($attempt=0;$attempt -lt 30 -and [VideoWindowBounds]::Fullscreen($handle);$attempt++){Start-Sleep -Milliseconds 200}
        if([VideoWindowBounds]::Fullscreen($handle)){throw 'Escape did not exit video fullscreen.'}
    }
    Close { $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
}
[pscustomobject]@{action=$Action;videoWindow=$true;name=$Name} | ConvertTo-Json -Compress
