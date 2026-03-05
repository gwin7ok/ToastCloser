param(
    [Parameter(Mandatory=$true)]
    [string]$SourcePng,
    [string]$OutPng = "${PSScriptRoot}\..\csharp\ToastCloser\Resources\ToastCloser_disabled.png",
    [int]$FuzzPercent = 10
)

<#
Usage:
 pwsh -NoProfile -File .\scripts\make-png-transparent.ps1 -SourcePng C:\path\to\input.png -OutPng .\csharp\ToastCloser\Resources\ToastCloser_disabled.png -FuzzPercent 10

This script will try to use ImageMagick's `magick` if available. If not, it falls back to a pure-PowerShell/.NET pixel loop which makes near-black pixels transparent.
FuzzPercent controls how tolerant the color matching is (0-100).
#>

Set-StrictMode -Version Latest

if (-not (Test-Path $SourcePng)) { Write-Error "Source PNG not found: $SourcePng"; exit 2 }

# Resolve output directory
$outDir = Split-Path $OutPng -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

# Try ImageMagick first
if (Get-Command magick -ErrorAction SilentlyContinue) {
    Write-Host "Using ImageMagick to make black transparent (fuzz=${FuzzPercent}%)"
    # Use -fuzz and -transparent black
    & magick convert "$SourcePng" -fuzz ${FuzzPercent}% -transparent black "$OutPng"
    exit $LASTEXITCODE
}

# Fallback: use .NET System.Drawing to process pixels
Write-Host "ImageMagick not found; using .NET pixel processing fallback (may be slower). Fuzz=${FuzzPercent}%"

try {
    Add-Type -AssemblyName System.Drawing
} catch {
    Write-Error "Failed to load System.Drawing. Ensure PowerShell on Windows or .NET support is available."
    exit 3
}

try {
    $bmp = [System.Drawing.Bitmap]::FromFile($SourcePng)
    $w = $bmp.Width
    $h = $bmp.Height
    $outBmp = New-Object System.Drawing.Bitmap $w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb

    # Precompute fuzz threshold in squared distance (RGB space)
    $fuzz = [math]::Max(0, [math]::Min(100, $FuzzPercent))
    # Map fuzz percent to squared distance (max distance between black and white is 3*255^2)
    $maxDist = 3 * 255 * 255
    $threshold = ([double]$fuzz / 100.0) * $maxDist

    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            # compute squared distance to black (0,0,0)
            $dist = [double]($c.R * $c.R + $c.G * $c.G + $c.B * $c.B)
            if ($dist -le $threshold) {
                # make fully transparent
                $outBmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, $c.R, $c.G, $c.B))
            } else {
                # keep original (preserve alpha)
                if ($c.A -lt 255) {
                    $outBmp.SetPixel($x, $y, $c)
                } else {
                    $outBmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $c.R, $c.G, $c.B))
                }
            }
        }
    }

    $outBmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $outBmp.Dispose()
    $bmp.Dispose()
    Write-Host "Saved transparent PNG: $OutPng"
    exit 0
} catch {
    Write-Error "Processing failed: $_"
    exit 4
}
