param()

$appId = 'ToastCloser.TestToast'
$xml = '<toast><visual><binding template="ToastGeneric"><text>www.youtube.com</text></binding></visual></toast>'
$doc = New-Object -TypeName 'Windows.Data.Xml.Dom.XmlDocument'
$doc.LoadXml($xml)
$toast = New-Object -TypeName 'Windows.UI.Notifications.ToastNotification' -ArgumentList $doc
$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId)
$notifier.Show($toast)
Write-Output "Sent WinRT minimal toast with AUMID=$appId"
