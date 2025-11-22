<#
Generate `Resources\ToastCloser.ico` from `Resources\ToastCloser.png` using the `tools/iconize` tool.

Usage:
  pwsh -File .\scripts\generate-ico.ps1

This script exits with non-zero on failure so CI jobs can call it explicitly and fail when icon generation fails.
#>
try {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot = Resolve-Path (Join-Path $scriptRoot '..')
    $png = Join-Path $repoRoot 'csharp\ToastCloser\Resources\ToastCloser.png'
    $ico = Join-Path $repoRoot 'csharp\ToastCloser\Resources\ToastCloser.ico'

    if (-not (Test-Path $png)) {
        Write-Error "Source PNG not found: $png"
        exit 2
    }

    Write-Output "Generating ICO from: $png -> $ico"

    $iconizeProj = Join-Path $repoRoot 'tools\iconize\iconize.csproj'
    $builtDll = Join-Path $repoRoot 'tools\iconize\bin\Release\net8.0-windows\iconize.dll'

    if (Test-Path $builtDll) {
        Write-Output "Using built DLL: $builtDll"
        & dotnet $builtDll $png $ico
        $rc = $LASTEXITCODE
    }
    else {
        if (-not (Test-Path $iconizeProj)) {
            Write-Error "iconize project not found: $iconizeProj"
            exit 3
        }
        $cmd = @(
            'run', '-c', 'Release', '--project', $iconizeProj, '--', $png, $ico
        )
        # Fallback to dotnet run (slower, but works when no build output exists)
        & dotnet @cmd
        $rc = $LASTEXITCODE
    }
    if ($rc -ne 0) {
        Write-Error "iconize failed with exit code $rc"
        exit $rc
    }

    Write-Output "Icon generation succeeded: $ico"
    exit 0
}
catch {
    Write-Error "Unexpected error: $_"
    exit 1
}
