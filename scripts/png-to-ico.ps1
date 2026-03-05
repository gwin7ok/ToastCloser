param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePng,
    [string]$OutIco = "${PSScriptRoot}\..\csharp\ToastCloser\Resources\ToastCloser_disabled.ico"
)

Set-StrictMode -Version Latest

Write-Host "png-to-ico.ps1: SourcePng=$SourcePng OutIco=$OutIco"

if (-not (Test-Path $SourcePng)) { Write-Error "Source PNG not found: $SourcePng"; exit 2 }

# Normalize paths
$src = (Resolve-Path $SourcePng).Path
$out = (Resolve-Path -LiteralPath (Split-Path $OutIco -Parent) -ErrorAction SilentlyContinue)
if (-not $out) {
    # create directory
    $dir = Split-Path $OutIco -Parent
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

# Locate iconize built DLL or project
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$iconizeDll = Join-Path $repoRoot 'tools\iconize\bin\Release\net8.0-windows\iconize.dll'
$iconizeProj = Join-Path $repoRoot 'tools\iconize\iconize.csproj'

if (Test-Path $iconizeDll) {
    Write-Host "Using iconize DLL: $iconizeDll"
    & dotnet $iconizeDll $src $OutIco
    exit $LASTEXITCODE
}
elseif (Test-Path $iconizeProj) {
    Write-Host "Building/running iconize project: $iconizeProj"
    & dotnet run -c Release --project $iconizeProj -- $src $OutIco
    exit $LASTEXITCODE
}
else {
    Write-Error "iconize tool not found. Please build tools/iconize or install ImageMagick and use it manually."
    exit 3
}
