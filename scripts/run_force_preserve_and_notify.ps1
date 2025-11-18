param(
    [int]$RunSeconds = 8
)
$proj = 'g:\Cursor_Folder\noticeWindowFinder\csharp\ToastCloser\ToastCloser.csproj'
Write-Output "Starting ToastCloser (displayLimitSeconds=1) for $RunSeconds seconds..."
$p = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--project',$proj,'--','--display-limit-seconds=1','--poll-interval-seconds=30','--preserve-history','--preserve-history-idle-ms=2000','--detection-timeout-ms=1000','--win-a-delay-ms=300') -PassThru
Start-Sleep -Seconds 1

# send BurntToast
if (-not (Get-Module -ListAvailable -Name BurntToast)) {
    Install-Module -Name BurntToast -Scope CurrentUser -Force -ErrorAction SilentlyContinue
}
Import-Module BurntToast -ErrorAction SilentlyContinue
Write-Output 'Sending test toast (force preserve-history)...'
try { New-BurntToastNotification -Text 'VSCode 強制テスト通知', '保存履歴テスト' } catch { Write-Output "Failed to send: $_" }
Start-Sleep -Seconds ($RunSeconds - 2)
if ($p -and $p.Id) { Stop-Process -Id $p.Id -Force }
Write-Output '---PROCESS STOPPED---'

# show log
$projDir = Split-Path $proj -Parent
$logFile = Get-ChildItem -Path (Join-Path $projDir 'bin') -Filter 'auto_closer.log' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($logFile -ne $null) { Write-Output "Found log: $($logFile.FullName)"; Get-Content -Path $logFile.FullName -Tail 400 } else { Write-Output 'auto_closer.log not found' }
