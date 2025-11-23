Set-StrictMode -Version Latest
Set-Location -LiteralPath 'G:\Cursor_Folder\ToastCloser'
Write-Host '--- .git folder size ---'
Get-ChildItem -LiteralPath .git -Recurse -Force | Measure-Object -Property Length -Sum | Format-List

Write-Host '--- git count-objects -vH ---'
git count-objects -vH

Write-Host '--- git status --porcelain --ignored ---'
git status --porcelain --ignored

Write-Host '--- pack files ---'
if (Test-Path .git\objects\pack) {
    Get-ChildItem -LiteralPath .git\objects\pack -Force | Sort-Object Length -Descending | Select-Object Name, @{Name='SizeMB';Expression={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
} else {
    Write-Host 'No pack folder'
}

Write-Host '--- reflog (20) ---'
git reflog -n 20

Write-Host '--- recent commits (20) ---'
git log --oneline -n 20
