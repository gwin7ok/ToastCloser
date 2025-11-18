# Dump likely registry locations that affect toast/banner behavior
Write-Output "--- Notifications base properties ---"
$k1 = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications'
if (Test-Path $k1) { Get-ItemProperty -Path $k1 | Format-List -Force } else { Write-Output "$k1 not found" }

Write-Output "--- PushNotifications policies (user/machine) ---"
$k2 = 'HKCU:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\PushNotifications'
if (Test-Path $k2) { Get-ItemProperty -Path $k2 | Format-List -Force } else { Write-Output "$k2 not found" }

$k3 = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\PushNotifications'
if (Test-Path $k3) { Get-ItemProperty -Path $k3 | Format-List -Force } else { Write-Output "$k3 not found" }

Write-Output "--- Focus Assist (Quiet Hours) keys (if present) ---"
$k4 = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\FocusAssist'
if (Test-Path $k4) { Get-ItemProperty -Path $k4 | Format-List -Force } else { Write-Output "$k4 not found" }

Write-Output "--- Recent Notifications\Settings keys (top 20) ---"
$base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
if (Test-Path $base) { Get-ChildItem -Path $base | Sort-Object LastWriteTime -Descending | Select-Object -First 20 | ForEach-Object { $_.Name } } else { Write-Output "$base not found" }
