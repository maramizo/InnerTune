$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$process = Get-Process InnerTune | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$bounds = $root.Current.BoundingRectangle
$root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        !$_.Current.BoundingRectangle.IsEmpty -and
        $_.Current.BoundingRectangle.Left -ge $bounds.Left -and
        $_.Current.BoundingRectangle.Right -le $bounds.Right -and
        $_.Current.BoundingRectangle.Bottom -le $bounds.Bottom -and
        $_.Current.BoundingRectangle.Top -ge ($bounds.Bottom - 100)
    } | Sort-Object { $_.Current.BoundingRectangle.Left } | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Current.Name
            Help = $_.Current.HelpText
            Left = $_.Current.BoundingRectangle.Left
            Top = $_.Current.BoundingRectangle.Top
        }
    } | Format-Table -AutoSize
