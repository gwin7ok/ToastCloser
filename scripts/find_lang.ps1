$targets = @("$env:USERPROFILE", "$env:APPDATA")
$patterns = @('LANG','chcp','DOTNET_CLI_UI_LANGUAGE')
foreach ($root in $targets) {
    Write-Host "Searching: $root"
    if (Test-Path $root) {
        Get-ChildItem -Path $root -Recurse -File -Include *.ps1,*.psm1,*.json,*.bashrc,*.bash_profile,*.profile -ErrorAction SilentlyContinue |
            Select-String -Pattern $patterns -SimpleMatch -ErrorAction SilentlyContinue |
            Select-Object Path,LineNumber,Line -Unique |
            ForEach-Object { Write-Host "FOUND: $($_.Path):$($_.LineNumber) -> $($_.Line.Trim())" }
    } else {
        Write-Host "<missing> $root"
    }
}
