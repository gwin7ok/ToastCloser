Param()
$mirror = Join-Path $PSScriptRoot '..\ToastCloser-mirror.git'
Set-Location -LiteralPath $mirror
Write-Output "Mirror repo: $mirror"
Write-Output '--- Checking publish/ entries ---'
$publish = git rev-list --objects --all | Select-String 'publish/' -SimpleMatch -AllMatches | ForEach-Object { $_.Line }
if ($publish) { $publish | Out-Host } else { Write-Output 'No publish/ entries found in mirror history' }
Write-Output ''
Write-Output '--- Top 20 objects by size (bytes) ---'
$items = git rev-list --objects --all | ForEach-Object {
  $parts = ($_ -split ' ',2)
  $sha = $parts[0]
  $path = if ($parts.Length -gt 1) { $parts[1] } else { '' }
  $size = git cat-file -s $sha 2>$null
  if ($size -ne $null) { [PSCustomObject]@{Size=[int]$size;Sha=$sha;Path=$path} }
}
$items | Sort-Object -Property Size -Descending | Select-Object -First 20 | ForEach-Object { "{0}`t{1}`t{2}" -f $_.Size, $_.Sha, $_.Path } | Out-Host

Return
