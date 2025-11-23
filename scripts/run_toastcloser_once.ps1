param(
	[int]$Seconds = 10
)

$proj = 'g:\Cursor_Folder\ToastCloser\csharp\ToastCloser\ToastCloser.csproj'
	$p = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--project',$proj,'--','--preserve-history','--preserve-history-idle-ms=2000','--detection-timeout-ms=1000','--win-shortcutkey-delay-ms=300') -PassThru
Start-Sleep -Seconds $Seconds
if ($p -and $p.Id) { Stop-Process -Id $p.Id -Force }
Write-Output '---PROCESS STOPPED---'

# Try to find the runtime log under the project's bin output folder
$projDir = Split-Path $proj -Parent
$logFile = Get-ChildItem -Path (Join-Path $projDir 'bin') -Filter 'auto_closer.log' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($logFile -ne $null) {
	Write-Output "Found log: $($logFile.FullName)"
	Get-Content -Path $logFile.FullName -Tail 200
} else {
	Write-Output 'auto_closer.log not found under bin output; searching project root'
	$fallback = Join-Path $projDir 'auto_closer.log'
	if (Test-Path $fallback) { Get-Content -Path $fallback -Tail 200 } else { Write-Output 'auto_closer.log not found' }
}
