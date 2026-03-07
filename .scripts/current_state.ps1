Param()
# Use repository root relative to this script for portability
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $RepoRoot
Write-Output '--- git status --porcelain ---'
git status --porcelain
Write-Output ''
Write-Output '--- HEAD (last 5) ---'
git log --oneline -n 5
Write-Output ''
Write-Output '--- tracked publish files (git ls-files) ---'
$tracked = git ls-files | Select-String 'publish/' -SimpleMatch | ForEach-Object { $_.Line }
if ($tracked -and $tracked.Count -gt 0) { $tracked | Out-Host } else { Write-Output 'No tracked publish files in current index' }
Write-Output ''
Write-Output '--- top 20 objects by size (bytes) in current repo history ---'
$items = git rev-list --objects --all | ForEach-Object {
  $parts = ($_ -split ' ',2)
  $sha = $parts[0]
  $path = if ($parts.Length -gt 1) { $parts[1] } else { '' }
  $size = git cat-file -s $sha 2>$null
  if ($size -ne $null) { [PSCustomObject]@{Size=[int]$size;Sha=$sha;Path=$path} }
}
$items | Sort-Object -Property Size -Descending | Select-Object -First 20 | ForEach-Object { Write-Output ("$($_.Size)`t$($_.Sha)`t$($_.Path)") }
Write-Output ''
Write-Output '--- backup and filtered paths existence ---'
$RepoParent = Split-Path -Parent $RepoRoot
$RepoName = Split-Path -Leaf $RepoRoot
Write-Output "ToastCloser .git.backup exists: $(Test-Path (Join-Path $RepoRoot '.git.backup'))"
Write-Output "filtered clone exists: $(Test-Path (Join-Path $RepoParent ("$RepoName-filtered")))"
Write-Output "mirror exists: $(Test-Path (Join-Path $RepoParent ("$RepoName-mirror.git")))"
Write-Output ''
Write-Output 'Done.'
Return
