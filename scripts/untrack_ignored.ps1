Set-Location 'G:\Cursor_Folder\ToastCloser'
$files = @(git ls-files -i --exclude-from=.gitignore --cached)
if ($files.Count -gt 0) {
  foreach ($f in $files) { git rm --cached --ignore-unmatch $f }
  git add .gitignore
  git commit -m 'gitignore: ignore build outputs and logs; untrack generated files' || Write-Output 'Nothing to commit'
} else {
  Write-Output 'No tracked files match .gitignore'
}
git status --porcelain
