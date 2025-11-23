# Query notification settings for ToastCloser AUMID(s)
$base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
if (-not (Test-Path $base)) {
    Write-Output "Notifications Settings key not found: $base"
    exit 0
}

Write-Output "Looking for keys matching 'ToastCloser' under $base"
Get-ChildItem -Path $base | Where-Object { $_.Name -match 'ToastCloser' } | ForEach-Object {
    Write-Output "---"
    Write-Output "Key: $($_.Name)"
    try {
        $props = Get-ItemProperty -Path $_.PsPath -ErrorAction Stop
        $props | Format-List -Force
    } catch {
        Write-Output "Failed to read properties: $_"
    }
}

Write-Output "If no matching keys, list a few recent keys under Notifications\Settings:"
Get-ChildItem -Path $base | Sort-Object LastWriteTime -Descending | Select-Object -First 10 | ForEach-Object { Write-Output $_.Name }
