param(
    [switch]$DryRun = $false,
    [switch]$SkipBuild = $false,
    [string]$RepoOwner = '',
    [string]$RepoName = ''
)

Write-Host "release-and-publish.ps1: DryRun=$DryRun SkipBuild=$SkipBuild RepoOwner=$RepoOwner RepoName=$RepoName"

if (-not $RepoOwner -or -not $RepoName) {
    # Try to auto-detect from git remote
    try {
        $url = git remote get-url origin 2>$null
        if ($url) {
            # parse owner/repo from URL
            if ($url -match '[:/]([^/]+)/([^/.]+)(?:\.git)?$') {
                if (-not $RepoOwner) { $RepoOwner = $matches[1] }
                if (-not $RepoName) { $RepoName = $matches[2] }
            }
        }
    } catch { }
}

Write-Host "Using RepoOwner=$RepoOwner RepoName=$RepoName"

if (-not $SkipBuild) {
    Write-Host "Building and publishing..."
    dotnet restore "csharp\ToastCloser\ToastCloser.csproj"
    dotnet publish "csharp\ToastCloser\ToastCloser.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o "publish\ToastCloser\win-x64"
} else {
    Write-Host "Skipping build (SkipBuild)"
}

# Post-build: package artifacts
if ($DryRun) {
    Write-Host "Dry run: would call post-build packaging with version from tag or csproj"
    Write-Host "Example command: pwsh .\scripts\post-build.ps1 -ProjectPath 'csharp\\ToastCloser\\ToastCloser.csproj' -ArtifactPrefix 'ToastCloser'"
} else {
    pwsh -NoProfile -File .\scripts\post-build.ps1 -ProjectPath 'csharp\ToastCloser\ToastCloser.csproj' -ArtifactPrefix 'ToastCloser'
}

# After packaging: upload via GH CLI if available, otherwise instruct user to upload
if (-not $DryRun) {
    $zipPattern = "${PWD}\ToastCloser_*.zip"
    $zips = Get-ChildItem -Path $zipPattern -ErrorAction SilentlyContinue
    if ($zips -and (Get-Command gh -ErrorAction SilentlyContinue)) {
        # Ensure gh is authenticated non-interactively using GITHUB_TOKEN when running in Actions
        if (-not $env:GITHUB_TOKEN) {
            Write-Host "Warning: GITHUB_TOKEN not set; gh authentication may fail."
        } else {
            Write-Host "Authenticating gh CLI using GITHUB_TOKEN"
            try {
                # Pipe the token into gh auth login --with-token for non-interactive login
                $env:GITHUB_TOKEN | gh auth login --with-token 2>$null
            } catch {
                Write-Host "gh auth login failed: $_"
            }
        }

        foreach ($z in $zips) {
            Write-Host "Uploading $($z.FullName) to GitHub Releases for $RepoOwner/$RepoName"
            # Determine tag name: prefer GITHUB_REF_NAME, fallback to parsing GITHUB_REF
            $tag = $env:GITHUB_REF_NAME
            if (-not $tag -and $env:GITHUB_REF -match 'refs/tags/(.+)') { $tag = $matches[1] }

            if (-not $tag) {
                Write-Host "Cannot determine tag name from environment; skipping upload of $($z.FullName)"
                continue
            }

            # Ensure a release exists for the tag; create if missing
            gh release view $tag --repo "$RepoOwner/$RepoName" > $null 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Release for tag $tag not found; creating release..."
                gh release create $tag --repo "$RepoOwner/$RepoName" --title $tag --notes "" > $null 2>&1
            }

            gh release upload $tag $($z.FullName) --repo "$RepoOwner/$RepoName" --clobber
        }
    } elseif ($zips) {
        Write-Host "No gh CLI available: artifacts created under current directory. Use GitHub web UI or REST API to upload."
    } else {
        Write-Host "No artifact zip found to upload."
    }
}

Write-Host "release-and-publish.ps1 complete"
