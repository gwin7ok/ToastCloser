Param()
# Use repo-relative filtered clone path
$RepoRoot = Split-Path -Parent $PSScriptRoot
$filtered = Join-Path (Split-Path -Parent $RepoRoot) ("$(Split-Path -Leaf $RepoRoot)-filtered")
if (-not (Test-Path $filtered)) { Write-Output "Filtered clone not found at $filtered"; exit 2 }
Set-Location -LiteralPath $filtered
## Detect git-filter-repo executable.
# Priority:
# 1) $env:GIT_FILTER_REPO
# 2) in PATH via Get-Command
# 3) common user Python Scripts locations
$exePath = $null
if ($env:GIT_FILTER_REPO) { $exePath = $env:GIT_FILTER_REPO }
if (-not $exePath) {
  $cmd = Get-Command git-filter-repo -ErrorAction SilentlyContinue
  if ($cmd) { $exePath = $cmd.Source }
}
if (-not $exePath) {
  $possible = @(
    Join-Path $Env:USERPROFILE 'AppData\Roaming\Python\Scripts\git-filter-repo.exe',
    Join-Path $Env:USERPROFILE 'AppData\Roaming\Python\Python313\Scripts\git-filter-repo.exe',
    Join-Path $Env:USERPROFILE 'AppData\Local\Programs\Python\Python313\Scripts\git-filter-repo.exe'
  )
  $exePath = $possible | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $exePath) {
  Write-Output "ERROR: git-filter-repo not found. Set GIT_FILTER_REPO or install git-filter-repo and ensure it's on PATH."; exit 2
}
Write-Output 'Running git-filter-repo --force (removing publish/**, **/bin/**, **/obj/**, tools/**/bin/**, tools/**/obj/**)...'
& $exePath --force --path-glob 'publish/**' --path-glob '**/bin/**' --path-glob '**/obj/**' --path-glob 'tools/**/bin/**' --path-glob 'tools/**/obj/**' --invert-paths
$rc = $LASTEXITCODE
Write-Output "git-filter-repo exit code: $rc"
if ($rc -ne 0) { Write-Output 'git-filter-repo failed'; exit $rc }
Write-Output 'Expiring reflog and running git gc...'
git reflog expire --expire=now --all
git gc --prune=now --aggressive

Write-Output '--- Checking publish/ entries ---'
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
