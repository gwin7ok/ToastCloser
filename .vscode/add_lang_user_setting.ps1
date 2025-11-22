$path = Join-Path $env:APPDATA 'Code\User\settings.json'
$bak  = $path + '.bak.' + (Get-Date -Format 'yyyyMMddHHmmss')

if (Test-Path $path) {
  Copy-Item -Path $path -Destination $bak -Force
}

$content = ''
if (Test-Path $path) { $content = Get-Content -Raw $path }

try {
  if ($content -and $content.Trim().Length -gt 0) {
    $json = ConvertFrom-Json $content -ErrorAction Stop
  } else {
    $json = [PSCustomObject]@{}
  }
} catch {
  $json = [PSCustomObject]@{}
}

# Build an ordered hashtable from parsed JSON (robust even if parsing returned a primitive)
$out = [ordered]@{}
if ($json -ne $null) {
  if ($json -is [System.Management.Automation.PSCustomObject]) {
    foreach ($prop in $json.PSObject.Properties) { $out[$prop.Name] = $prop.Value }
  }
}

# Ensure terminal env override
$out['terminal.integrated.env.windows'] = @{ LANG = '' }

# Write back the merged object
$out | ConvertTo-Json -Depth 10 | Set-Content -Path $path -Encoding UTF8

Write-Host 'BACKUP:' $bak
Write-Host 'UPDATED:' $path
Write-Host '--- NEW CONTENT ---'
Get-Content -Raw $path
Write-Host '--- END ---'
