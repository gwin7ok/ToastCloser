<#
Generate release body for a given tag from `CHANGELOG.md`.

Usage:
  pwsh .\scripts\generate-release-body.ps1 -Tag v1.2.3 -OutFile release-body.md

If `-Tag` is omitted, script tries to use the tag from `GITHUB_REF_NAME` or the latest git tag.
#>

[CmdletBinding()]
param(
    [string]$Tag = '',
    [string]$ChangelogPath = "CHANGELOG.md",
    [string]$OutFile = ''
)

function Get-LatestTag {
    $t = git describe --tags --abbrev=0 2>$null
    return $t.Trim()
}

if (-not (Test-Path $ChangelogPath)) {
    Write-Error "$ChangelogPath not found"
    exit 2
}

if (-not $Tag) {
    if ($env:GITHUB_REF_NAME) { $Tag = $env:GITHUB_REF_NAME }
    else { $Tag = Get-LatestTag }
}

if (-not $Tag) {
    Write-Error "Tag could not be determined. Provide -Tag or set GITHUB_REF_NAME or have annotated tags in git."
    exit 3
}

$all = Get-Content $ChangelogPath -ErrorAction Stop

# find header line matching the tag. Accept formats: '## [v1.2.3]' or '## v1.2.3' or '## [v1.2.3] - date'
$pattern = '^\s*##\s*\[?' + [regex]::Escape($Tag) + '\]?'
$start = -1
for ($i = 0; $i -lt $all.Length; $i++) {
    if ($all[$i] -match $pattern) { $start = $i; break }
}

if ($start -eq -1) {
    Write-Warning "No section for tag '$Tag' found in $ChangelogPath. Falling back to top of file."
    $start = 0
}

# collect until next '## ' or end
$bodyLines = @()
for ($j = $start; $j -lt $all.Length; $j++) {
    $line = $all[$j]
    if ($j -ne $start -and $line -match '^\s*##\s+' ) { break }
    $bodyLines += $line
}

# Trim leading/trailing blank lines
while ($bodyLines.Count -gt 0 -and ($bodyLines[0].Trim() -eq '')) { $bodyLines = $bodyLines[1..($bodyLines.Count-1)] }
while ($bodyLines.Count -gt 0 -and ($bodyLines[-1].Trim() -eq '')) { $bodyLines = $bodyLines[0..($bodyLines.Count-2)] }

if ($OutFile) {
    $bodyLines | Out-File -FilePath $OutFile -Encoding UTF8
    Write-Output "Wrote release body to $OutFile"
} else {
    $bodyLines | ForEach-Object { Write-Output $_ }
}
