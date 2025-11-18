Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$name = '新しい通知'
$class = 'Windows.UI.Core.CoreWindow'
$nameCond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty, $name)
$classCond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty, $class)
$controlCond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
$and = New-Object System.Windows.Automation.AndCondition($nameCond,$classCond,$controlCond)
$root = [System.Windows.Automation.AutomationElement]::RootElement
$found = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, $and)
Write-Output "UIA found count=$($found.Count)"
for ($i=0; $i -lt $found.Count; $i++) {
    $el = $found[$i]
    $hwnd = $el.Current.NativeWindowHandle
    Write-Output ("  idx=$i Name='$($el.Current.Name)' Class='$($el.Current.ClassName)' NativeWindowHandle=0x{0:X}" -f $hwnd)
}
