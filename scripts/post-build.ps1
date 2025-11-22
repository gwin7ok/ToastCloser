param(
    [string]$ProjectPath = "csharp\ToastCloser\ToastCloser.csproj",
    [string]$ArtifactPrefix = "ToastCloser",
    [string]$Configuration = "Release",
    [switch]$SkipZip = $false
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

$version = Get-VersionFromCsProj $ProjectPath
if (-not $version) {
    $version = $env:GITHUB_REF_NAME
}
if (-not $version) { $version = "0.0.0" }

Write-Host "Determined version: $version"

# Locate publish folder (example layout)
$publishDir = Join-Path -Path (Split-Path -Parent $ProjectPath) -ChildPath "bin\$Configuration\net8.0-windows\publish"
if (-not (Test-Path $publishDir)) {
    # fallback to common publish path used by workflow
    $publishDir = Join-Path $PWD "publish\ToastCloser\win-x64"
}

Write-Host "Publish directory: $publishDir"

if (-not $SkipZip) {
    $arch = "win-x64"
    $zipName = "${ArtifactPrefix}_${version}_${arch}.zip"
    $zipPath = Join-Path $PWD $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "Creating zip: $zipPath from $publishDir"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath)
    Write-Host "Created $zipPath"
} else {
    Write-Host "Skipping zip creation (SkipZip)"
}

Write-Host "post-build.ps1 complete"
param(
    [string]$ProjectPath = "csharp/ToastCloser/ToastCloser.csproj",
    [string]$ArtifactPrefix = "ToastCloser",
    [switch]$DryRun
)

Write-Host "post-build.ps1: ProjectPath=$ProjectPath ArtifactPrefix=$ArtifactPrefix DryRun=$DryRun"

$publishDir = Join-Path -Path (Get-Location) -ChildPath "artifacts\win-x64"
if (-not (Test-Path $publishDir)) {
    Write-Host "Publish directory not found: $publishDir"
    if ($DryRun) { exit 0 }
    throw "Publish output not found"
}

# Call Python helper to package artifacts and determine version
$py = "python"
$script = Join-Path -Path (Split-Path -Parent $MyInvocation.MyCommand.Definition) -ChildPath "post-build.py"

$args = @()
$args += "-publishDir"; $args += $publishDir
$args += "-artifactPrefix"; $args += $ArtifactPrefix
$args += "-arch"; $args += "win-x64"

if ($DryRun) {
    Write-Host "DRYRUN: Would run: $py $script $($args -join ' ')"
    exit 0
}

Write-Host "Running packaging helper..."
& $py $script $args
if ($LASTEXITCODE -ne 0) { throw "post-build.py failed with code $LASTEXITCODE" }

Write-Host "post-build completed."
