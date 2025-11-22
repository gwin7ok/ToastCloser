param(
    [string]$ProjectPath = "csharp\ToastCloser\ToastCloser.csproj",
    [string]$ArtifactPrefix = "ToastCloser",
    [string]$Configuration = "Release",
    [switch]$SkipZip = $false,
    [string]$ReleaseVersion = '',
    [switch]$UseTempDir = $false
)

Write-Host "post-build.ps1: ProjectPath=$ProjectPath ArtifactPrefix=$ArtifactPrefix Configuration=$Configuration SkipZip=$SkipZip"

# Determine version: try to read from the csproj Version property or fallback to tag from env
function Get-VersionFromCsProj($csproj)
{
    if (-not (Test-Path $csproj)) { return $null }
    try {
        [xml]$x = Get-Content $csproj -Raw
        $ns = $x.Project.PropertyGroup
        foreach ($pg in $ns) {
            if ($pg.Version) { return $pg.Version }
        }
    } catch { }
    return $null
}

$version = $null
if ($ReleaseVersion -and $ReleaseVersion.Trim() -ne '') { $version = $ReleaseVersion }
if (-not $version) { $version = Get-VersionFromCsProj $ProjectPath }
if (-not $version) { $version = $env:GITHUB_REF_NAME }
if (-not $version) { $version = "0.0.0" }

Write-Host "Determined version: $version"

# Locate publish folder: try several candidate locations used by CI and local dev
$cwd = $PWD.Path
$projectDir = Split-Path -Parent $ProjectPath
$candidates = @(
    Join-Path $cwd 'publish\ToastCloser\win-x64'
    Join-Path $cwd 'artifacts\win-x64'
    Join-Path $projectDir "bin\$Configuration\net8.0-windows\publish"
    Join-Path $cwd 'publish'
)

$publishDir = $null
foreach ($d in $candidates) {
    if (Test-Path $d) { $publishDir = $d; break }
}

if (-not $publishDir) {
    Write-Error "Publish directory not found. Checked:`n  $($candidates -join "`n  ")"
    exit 1
}

Write-Host "Publish directory: $publishDir"

if (-not $SkipZip) {
    $arch = "win-x64"
    $zipName = "${ArtifactPrefix}_${version}_${arch}.zip"
    if ($UseTempDir) {
        $temp = [IO.Path]::GetTempPath()
        $zipPath = Join-Path $temp $zipName
    } else {
        $zipPath = Join-Path $PWD $zipName
    }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "Creating zip: $zipPath from $publishDir"
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
        [System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath)
        Write-Host "Created $zipPath"
    } catch {
        Write-Error "Failed to create zip from '$publishDir' -> $($_.Exception.Message)"
        exit 1
    }
} else {
    Write-Host "Skipping zip creation (SkipZip)"
}

Write-Host "post-build.ps1 complete"
Write-Host "post-build.ps1 complete"
