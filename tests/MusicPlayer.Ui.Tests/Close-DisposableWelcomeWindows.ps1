$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Welcome-window cleanup requires a disposable runner.'}
if(Get-Process MusicPlayer -ErrorAction SilentlyContinue){throw 'Welcome-window cleanup must precede player launch.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class DisposableWelcomeWindow {
    delegate bool Callback(IntPtr window,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback,IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder title,int count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint id);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    public static IntPtr[] Find() {
        var result=new List<IntPtr>();
        EnumWindows((window,data)=>{
            var title=new StringBuilder(256);GetWindowText(window,title,title.Capacity);
            if(IsWindowVisible(window)&&title.ToString()=="Microsoft account")result.Add(window);
            return true;
        },IntPtr.Zero);
        return result.ToArray();
    }
}
'@
for($attempt=0;$attempt -lt 12;$attempt++){
    $visible=@([DisposableWelcomeWindow]::Find())
    if($visible.Count -eq 0){return}
    foreach($handle in $visible){
        $ownerId=[uint32]0
        $null=[DisposableWelcomeWindow]::GetWindowThreadProcessId($handle,[ref]$ownerId)
        $owner=Get-Process -Id $ownerId
        if($owner.SessionId -ne (Get-Process -Id $PID).SessionId -or !$owner.Path -or
            !$owner.Path.StartsWith($env:WINDIR+'\',[StringComparison]::OrdinalIgnoreCase)){
            throw 'The account welcome window is not owned by the disposable Windows system session.'
        }
        Write-Host "Closing optional Microsoft-account welcome UI owned by $($owner.ProcessName)."
        $window=[Windows.Automation.AutomationElement]::FromHandle($handle)
        $pattern=$null
        if($window.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern,[ref]$pattern)){$pattern.Close()}
        else {
            $close=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Close')
            $button=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$close)
            if($button){$button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
            elseif(![DisposableWelcomeWindow]::PostMessage($handle,0x10,[IntPtr]::Zero,[IntPtr]::Zero)){throw 'The optional Windows account welcome UI rejected WM_CLOSE.'}
        }
    }
    Start-Sleep -Milliseconds 250
}
throw 'The optional Windows account welcome UI did not close.'
