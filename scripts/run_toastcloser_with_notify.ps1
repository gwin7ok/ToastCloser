param(
    [int]$RunSeconds = 12
)
$proj = 'g:\Cursor_Folder\noticeWindowFinder\csharp\ToastCloser\ToastCloser.csproj'
Write-Output "Starting ToastCloser for $RunSeconds seconds..."
    $p = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--project',$proj,'--','--preserve-history','--preserve-history-idle-ms=2000','--detection-timeout-ms=1000','--win-a-delay-ms=300') -PassThru
# wait a moment for the app to initialize
Start-Sleep -Seconds 2

# ensure BurntToast available and send notification
if (-not (Get-Module -ListAvailable -Name BurntToast)) {
    Write-Output 'Installing BurntToast (CurrentUser)...'
    Install-Module -Name BurntToast -Scope CurrentUser -Force -ErrorAction SilentlyContinue
}
Import-Module BurntToast -ErrorAction SilentlyContinue
Write-Output 'Sending test toast...'
try {
    New-BurntToastNotification -Text 'VSCode 自動テスト通知', 'この通知は自動テストによるものです'
} catch {
    Write-Output "Failed to send BurntToast notification: $_"
}

# wait while ToastCloser can detect it
Start-Sleep -Seconds ($RunSeconds - 3)

if ($p -and $p.Id) { Stop-Process -Id $p.Id -Force }
Write-Output '---PROCESS STOPPED---'

# locate the runtime log under bin
$projDir = Split-Path $proj -Parent
$logFile = Get-ChildItem -Path (Join-Path $projDir 'bin') -Filter 'auto_closer.log' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($logFile -ne $null) {
    Write-Output "Found log: $($logFile.FullName)"
    Get-Content -Path $logFile.FullName -Tail 400
} else {
    Write-Output 'auto_closer.log not found under bin output; searching project root'
    $fallback = Join-Path $projDir 'auto_closer.log'
    if (Test-Path $fallback) { Get-Content -Path $fallback -Tail 400 } else { Write-Output 'auto_closer.log not found' }
}
