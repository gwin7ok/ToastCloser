Param()
$root = 'G:\\Cursor_Folder'
Set-Location -LiteralPath $root
$filtered = Join-Path $root 'ToastCloser-filtered'
if (Test-Path $filtered) { Remove-Item -LiteralPath $filtered -Recurse -Force }
Write-Output 'Cloning mirror into non-bare filtered clone...'
git clone .\ToastCloser-mirror.git .\ToastCloser-filtered
Set-Location -LiteralPath $filtered
Write-Output "Filtered clone path: $(Get-Location)"
$exe = 'C:\\Users\\naoki\\AppData\\Roaming\\Python\\Python313\\Scripts\\git-filter-repo.exe'
if (-not (Test-Path $exe)) { Write-Output "ERROR: git-filter-repo not found at: $exe"; exit 2 }
Write-Output 'Running git-filter-repo to remove publish/**, **/bin/**, **/obj/**, tools/**/bin/**, tools/**/obj/**...'
& $exe --path-glob 'publish/**' --path-glob '**/bin/**' --path-glob '**/obj/**' --path-glob 'tools/**/bin/**' --path-glob 'tools/**/obj/**' --invert-paths
$rc = $LASTEXITCODE
Write-Output "git-filter-repo exit code: $rc"
if ($rc -ne 0) { Write-Output 'git-filter-repo failed'; exit $rc }
Write-Output 'Expiring reflog and running git gc...'
git reflog expire --expire=now --all
git gc --prune=now --aggressive

Write-Output '--- Checking publish/ entries in filtered clone ---'
$publish = git rev-list --objects --all | Select-String 'publish/' -SimpleMatch -AllMatches | ForEach-Object { $_.Line }
if ($publish -and $publish.Count -gt 0) { $publish | Out-Host } else { Write-Output 'No publish/ entries found in filtered clone' }

Write-Output ''
Write-Output '--- Top 20 objects by size (bytes) ---'
$items = git rev-list --objects --all | ForEach-Object {
  $parts = ($_ -split ' ',2)
  $sha = $parts[0]
  $path = if ($parts.Length -gt 1) { $parts[1] } else { '' }
  $size = git cat-file -s $sha 2>$null
  if ($size -ne $null) { [PSCustomObject]@{Size=[int]$size;Sha=$sha;Path=$path} }
}
$items | Sort-Object -Property Size -Descending | Select-Object -First 20 | ForEach-Object { Write-Output ("$($_.Size)`t$($_.Sha)`t$($_.Path)") }

Write-Output 'Done.'
Return
