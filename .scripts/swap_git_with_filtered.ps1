Param()
$orig = 'G:\\Cursor_Folder\\ToastCloser'
$filtered = 'G:\\Cursor_Folder\\ToastCloser-filtered'
Set-Location -LiteralPath $orig
Write-Output "Original repo: $orig"
if (-not (Test-Path $filtered)) { Write-Output "ERROR: filtered repo not found at $filtered"; exit 2 }

# Backup original .git
if (Test-Path (Join-Path $orig '.git')) {
  $backup = Join-Path $orig '.git.backup'
  Write-Output "Backing up existing .git to: $backup"
  if (Test-Path $backup) { Write-Output "Removing previous backup: $backup"; Remove-Item -LiteralPath $backup -Recurse -Force }
  Rename-Item -LiteralPath (Join-Path $orig '.git') -NewName '.git.backup'
} else {
  Write-Output 'No existing .git found in original repo; continuing.'
}

# Copy filtered .git into place
$srcGit = Join-Path $filtered '.git'
$dstGit = Join-Path $orig '.git'
Write-Output "Copying filtered .git from $srcGit to $dstGit (this may take a moment)..."
Copy-Item -LiteralPath $srcGit -Destination $dstGit -Recurse -Force

# Verify
Write-Output 'Verifying git status and top commit...'
Set-Location -LiteralPath $orig
$st = git status --porcelain 2>&1
Write-Output '--- git status --porcelain ---'
Write-Output $st
Write-Output '--- HEAD commit ---'
git log --oneline -n 1

# Check for publish tracked files
Write-Output '--- Check for tracked publish/ entries ---'
$tracked = git ls-files | Select-String 'publish/' -SimpleMatch -Quiet
if ($tracked) { git ls-files | Select-String 'publish/' -SimpleMatch | ForEach-Object { $_ } } else { Write-Output 'No tracked publish/ entries in current index' }

Write-Output 'Swap complete. If everything looks good you may remove .git.backup later.'
Return
