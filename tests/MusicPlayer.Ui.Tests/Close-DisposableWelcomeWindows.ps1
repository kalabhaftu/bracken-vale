$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Welcome-window cleanup requires a disposable runner.'}
if(Get-Process MusicPlayer -ErrorAction SilentlyContinue){throw 'Welcome-window cleanup must precede player launch.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$root=[Windows.Automation.AutomationElement]::RootElement
$name=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Microsoft account')
for($attempt=0;$attempt -lt 12;$attempt++){
    $windows=$root.FindAll([Windows.Automation.TreeScope]::Children,$name)
    $visible=@($windows | Where-Object {!$_.Current.IsOffscreen})
    if($visible.Count -eq 0){return}
    foreach($window in $visible){
        $owner=Get-Process -Id $window.Current.ProcessId
        if($owner.SessionId -ne (Get-Process -Id $PID).SessionId -or !$owner.Path -or
            !$owner.Path.StartsWith($env:WINDIR+'\',[StringComparison]::OrdinalIgnoreCase)){
            throw 'The account welcome window is not owned by the disposable Windows system session.'
        }
        Write-Host "Closing optional Microsoft-account welcome UI owned by $($owner.ProcessName)."
        $pattern=$null
        if($window.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern,[ref]$pattern)){$pattern.Close()}
        else {
            $close=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Close')
            $button=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$close)
            if(!$button){throw 'The optional Windows account welcome UI has no close action.'}
            $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        }
    }
    Start-Sleep -Milliseconds 250
}
throw 'The optional Windows account welcome UI did not close.'
