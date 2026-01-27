param(
    [string]$RepoOwner = '',
    [string]$RepoName = '',
    [switch]$DryRun = $false
)

Set-StrictMode -Version Latest

Write-Host "release-tag-and-push-short.ps1: DryRun=$DryRun RepoOwner=$RepoOwner RepoName=$RepoName"

# Determine tag from Directory.Build.props Version element
$propsPath = Join-Path $PSScriptRoot '..\Directory.Build.props' | Resolve-Path -ErrorAction SilentlyContinue
if (-not $propsPath) {
    Write-Error "Directory.Build.props not found at expected location: $PSScriptRoot\..\Directory.Build.props"
    exit 2
}

[xml]$xml = Get-Content $propsPath
$ver = $xml.Project.PropertyGroup.Version
if (-not $ver) {
    Write-Error "<Version> not found in Directory.Build.props"
    exit 3
}

$tag = $ver.Trim()
if ($tag -notmatch '^v') { $tag = "v$tag" }
Write-Host "Resolved tag: $tag"

if ($DryRun) {
    Write-Host "DryRun: would recreate tag $tag and push to origin"
    exit 0
}

# Ensure we have a git repo
try { git rev-parse --is-inside-work-tree > $null 2>&1 } catch { Write-Error "Not a git repo"; exit 4 }

# Optionally detect repo owner/name if not provided
if (-not $RepoOwner -or -not $RepoName) {
    try {
        $url = git remote get-url origin 2>$null
        if ($url -match '[:/]([^/]+)/([^/.]+)(?:\.git)?$') {
            if (-not $RepoOwner) { $RepoOwner = $Matches[1] }
            if (-not $RepoName) { $RepoName = $Matches[2] }
        }
    }
    catch { }
}

Write-Host "Using repo: $RepoOwner/$RepoName"

# Delete remote tag if exists
Write-Host "Checking remote tag origin/$tag"
$existsRemote = $false
try {
    $raw = git ls-remote --tags origin "refs/tags/$tag" 2>$null
    if ($raw -and $raw.Trim() -ne '') { $existsRemote = $true }
}
catch { }

if ($existsRemote) {
    Write-Host "Deleting remote tag origin/$tag"
    git push origin --delete $tag
    if ($LASTEXITCODE -ne 0) { Write-Warning "Failed to delete remote tag origin/$tag (continuing)" }
}

# Delete local tag if exists
$existsLocal = $false
try { 
    git rev-parse -q --verify "refs/tags/$tag" > $null 2>&1; if ($LASTEXITCODE -eq 0) { $existsLocal = $true }
}
catch { }

if ($existsLocal) {
    Write-Host "Deleting local tag $tag"
    git tag -d $tag
    if ($LASTEXITCODE -ne 0) { Write-Warning "Failed to delete local tag $tag (continuing)" }
}

# Create annotated tag on current HEAD
Write-Host "Creating annotated tag $tag on HEAD"
git tag -a $tag -m "$tag"
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create tag $tag"; exit 10 }

# Push tag to origin
Write-Host "Pushing tag $tag to origin"
git push origin $tag
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to push tag $tag to origin"; exit 11 }

Write-Host "Tag $tag recreated and pushed to origin successfully. If repository has Actions workflow listening for tag push (e.g. refs/tags/v*), it should trigger now."
exit 0
