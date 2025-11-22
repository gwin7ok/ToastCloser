param(
    [switch]$DryRun = $false,
    [switch]$SkipBuild = $false,
    [string]$RepoOwner = '',
    [string]$RepoName = '',
    [switch]$AllowLocalBuild = $false
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

# Determine whether we're running in CI (GitHub Actions)
$isCI = $false
if ($env:GITHUB_ACTIONS -and $env:GITHUB_ACTIONS -eq 'true') { $isCI = $true }

# If not in CI and local builds are not explicitly allowed, skip building and zip creation
if (-not $isCI -and -not $AllowLocalBuild) {
    Write-Host "Local run detected and local builds are not allowed. Skipping build and archive generation by default."
    $SkipBuild = $true
    $skipPostBuild = $true
} else {
    $skipPostBuild = $false
}

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
    if ($skipPostBuild) {
        Write-Host "Skipping post-build (archive generation) because this is a local run and local builds are not allowed."
        # Locate any pre-existing zip artifacts in repository if present
        $zipPattern = "${PWD}\ToastCloser_*.zip"
        $zips = Get-ChildItem -Path $zipPattern -ErrorAction SilentlyContinue
        if (-not $zips) {
            Write-Host "No artifact zip found in repository. To enable building locally pass -AllowLocalBuild to this script or create the archive manually."
        }
    } else {
    # Determine tag (if available) to pass to post-build so the archive name includes the correct version
    $tagForBuild = $env:GITHUB_REF_NAME
    if (-not $tagForBuild -and $env:GITHUB_REF -match 'refs/tags/(.+)') { $tagForBuild = $matches[1] }

    $useTemp = $false
    if ($env:GITHUB_ACTIONS -and $env:GITHUB_ACTIONS -eq 'true') { $useTemp = $true }

    $pbArgsArray = @('-ProjectPath', 'csharp\ToastCloser\ToastCloser.csproj', '-ArtifactPrefix', 'ToastCloser')
    if ($tagForBuild) { $pbArgsArray += @('-ReleaseVersion', $tagForBuild) }
    if ($useTemp) { $pbArgsArray += '-UseTempDir' }

    Write-Host "Calling post-build with: $($pbArgsArray -join ' ')"
    # Capture output from post-build to find created zip path (supports creating zip in temp during CI)
    $pbOutput = & pwsh -NoProfile -File .\scripts\post-build.ps1 @pbArgsArray 2>&1
    Write-Host $pbOutput

    # Try to extract created zip path from output (line starts with 'Created ')
    $createdZip = $null
    foreach ($line in $pbOutput -split "`n") {
        if ($line -match 'Created\s+(.*)') { $createdZip = $Matches[1].Trim(); break }
    }
    if ($createdZip) {
        Write-Host "Detected created zip: $createdZip"
        # use the created zip as the artifact to upload
        $zips = @(Get-Item -LiteralPath $createdZip -ErrorAction SilentlyContinue)
    } else {
        # fallback to locating zips under PWD
        $zipPattern = "${PWD}\ToastCloser_*.zip"
        $zips = Get-ChildItem -Path $zipPattern -ErrorAction SilentlyContinue
    }
    }
}

# After packaging: upload via GH CLI if available, otherwise instruct user to upload
if (-not $DryRun) {
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
            # Determine notes file: prefer RELEASE_NOTES_FILE env (set by CI), otherwise generate from CHANGELOG.md
            $notesFile = $null
            if ($env:RELEASE_NOTES_FILE -and (Test-Path $env:RELEASE_NOTES_FILE)) {
                $notesFile = (Resolve-Path $env:RELEASE_NOTES_FILE).Path
            } else {
                $notesFile = Join-Path $PWD ("release-notes-$tag.md")
                try {
                    pwsh -NoProfile -File .\scripts\generate-release-body.ps1 -Tag $tag -OutFile $notesFile 2>$null
                    if (-not (Test-Path $notesFile)) { $notesFile = $null }
                } catch {
                    Write-Host "generate-release-body failed: $_"
                    $notesFile = $null
                }
            }

            # If release does not exist, create. If exists, edit notes to overwrite.
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Release for tag $tag not found; creating release..."
                if ($notesFile) {
                    gh release create $tag --repo "$RepoOwner/$RepoName" --title $tag --notes-file $notesFile > $null 2>&1
                } else {
                    gh release create $tag --repo "$RepoOwner/$RepoName" --title $tag --notes "" > $null 2>&1
                }
            } else {
                Write-Host "Release for tag $tag already exists; updating release notes (overwrite)..."
                if ($notesFile) {
                    gh release edit $tag --repo "$RepoOwner/$RepoName" --notes-file $notesFile > $null 2>&1
                } else {
                    gh release edit $tag --repo "$RepoOwner/$RepoName" --notes "" > $null 2>&1
                }
            }

            # Upload asset, overwriting existing asset with same name
            gh release upload $tag $($z.FullName) --repo "$RepoOwner/$RepoName" --clobber
        }
    } elseif ($zips) {
        Write-Host "No gh CLI available: artifacts created under current directory. Use GitHub web UI or REST API to upload."
    } else {
        Write-Host "No artifact zip found to upload."
    }
}

Write-Host "release-and-publish.ps1 complete"
