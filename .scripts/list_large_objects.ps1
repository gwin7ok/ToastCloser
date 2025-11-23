Param()
Set-Location -LiteralPath 'G:\Cursor_Folder\ToastCloser'
Write-Output '--- publish/ entries in history ---'
$publish = git rev-list --objects --all | Select-String 'publish/' -SimpleMatch -AllMatches | ForEach-Object { $_.Line }
if ($publish) { $publish | Out-Host } else { Write-Output 'No publish/ entries found in history' }
Write-Output ''
Write-Output '--- top 50 objects by size (bytes) ---'
git rev-list --objects --all | ForEach-Object {
  $parts = $_ -split ' ',2
  $sha = $parts[0]
  $path = if ($parts.Length -gt 1) { $parts[1] } else { '' }
  $size = git cat-file -s $sha 2>$null
  if ($size -ne $null) {
    "{0}`t{1}`t{2}" -f $size, $sha, $path
  }
} | Sort-Object { [int]($_ -split "`t")[0] } -Descending | Select-Object -First 50 | ForEach-Object {
  $cols = $_ -split "`t",3
  $size = [int]$cols[0]; $sha=$cols[1]; $path=$cols[2]
  $h = if ($path) { $path } else { '(no path)' }
  "{0,12:N0} bytes`t{1}`t{2}" -f $size, $sha, $h
} | Out-Host

Return
