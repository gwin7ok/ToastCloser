Param()
Set-Location -LiteralPath 'G:\Cursor_Folder\ToastCloser'
Write-Output 'Reading .gitignore rules...'
$gitignore = Get-Content .gitignore -ErrorAction SilentlyContinue | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not ($_ -like '#*') }
if (-not $gitignore) { Write-Output 'No .gitignore rules found.'; exit 0 }
Write-Output "Rules count: $($gitignore.Count)"
Write-Output ''

Write-Output 'Collecting all paths from history (this may take a few seconds)...'
$historyPaths = git rev-list --objects --all | ForEach-Object { ($_.Split(' ',2))[1] } | Where-Object { $_ }
Write-Output "Total historical paths: $($historyPaths.Count)"
Write-Output ''

Write-Output 'Filtering paths that match .gitignore rules using git check-ignore...'
# git check-ignore respects .gitignore; feed candidate paths via stdin
$historyPaths | git check-ignore --stdin -v 2>$null | ForEach-Object { $_ } | Sort-Object -Unique | ForEach-Object { Write-Output $_ }

Write-Output ''
Write-Output 'If you want to proceed with removal, run the filter-repo step after reviewing these paths.'
Return
