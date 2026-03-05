param(
    [Parameter(Mandatory=$true)]
    [string]$SourcePng,
    [string]$OutPng = "${PSScriptRoot}\..\csharp\ToastCloser\Resources\ToastCloser_disabled.png",
    [int]$DesaturatePercent = 100
)

Set-StrictMode -Version Latest

if (-not (Test-Path $SourcePng)) { Write-Error "Source PNG not found: $SourcePng"; exit 2 }

# Ensure output directory exists
$outDir = Split-Path $OutPng -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

# Normalize DesaturatePercent
$DesaturatePercent = [math]::Max(0, [math]::Min(100, $DesaturatePercent))

# If ImageMagick available, prefer it for speed/quality
if (Get-Command magick -ErrorAction SilentlyContinue) {
    Write-Host "Using ImageMagick to desaturate ($DesaturatePercent%) -> $OutPng"
    if ($DesaturatePercent -ge 100) {
        # full grayscale, preserve alpha
        & magick convert "$SourcePng" -alpha set -colorspace Gray "$OutPng"
    }
    else {
        # partial desaturation: use +level-color? simpler: compose grayscale and lerp using -channel
        $tmp = [System.IO.Path]::GetTempFileName() + ".png"
        & magick convert "$SourcePng" -alpha set -colorspace Gray "$tmp"
        # lerp between original and gray using -evaluate-sequence? use -fx to mix
        $mix = [double]($DesaturatePercent) / 100.0
        & magick convert "$SourcePng" "$tmp" -alpha set -compose blend -define compose:args=${DesaturatePercent},$([int](100-$DesaturatePercent)) -composite "$OutPng"
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
    exit $LASTEXITCODE
}

# Fallback: use .NET pixel processing
Write-Host "ImageMagick not found; using .NET pixel processing fallback (may be slower). Desaturate=${DesaturatePercent}%"
try { Add-Type -AssemblyName System.Drawing } catch { Write-Error "Failed to load System.Drawing"; exit 3 }

try {
    $bmp = [System.Drawing.Bitmap]::FromFile($SourcePng)
    $w = $bmp.Width
    $h = $bmp.Height
    $outBmp = New-Object System.Drawing.Bitmap $w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb

    $mix = [double]$DesaturatePercent / 100.0
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x,$y)
            # preserve fully transparent pixels
            if ($c.A -eq 0) { $outBmp.SetPixel($x,$y,$c); continue }
            # compute luminance
            $lum = [int]([math]::Round(0.299*$c.R + 0.587*$c.G + 0.114*$c.B))
            $r = [int]([math]::Round((1-$mix)*$c.R + $mix*$lum))
            $g = [int]([math]::Round((1-$mix)*$c.G + $mix*$lum))
            $b = [int]([math]::Round((1-$mix)*$c.B + $mix*$lum))
            $outBmp.SetPixel($x,$y,[System.Drawing.Color]::FromArgb($c.A,$r,$g,$b))
        }
    }
    $outBmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $outBmp.Dispose(); $bmp.Dispose()
    Write-Host "Saved grayout PNG: $OutPng"
    exit 0
} catch {
    Write-Error "Processing failed: $_"
    exit 4
}
