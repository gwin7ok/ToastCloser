param(
    [int]$minSeconds = 10,
    [int]$maxSeconds = 30
)

# Activate venv if present
if (Test-Path .venv) {
    . .\.venv\Scripts\Activate.ps1
}

# Run in background
Start-Process -FilePath python -ArgumentList "src\main.py --min-seconds $minSeconds --max-seconds $maxSeconds" -WindowStyle Hidden
