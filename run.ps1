param(
    [int]$displayLimitSeconds = 10,
    [int]$pollIntervalSeconds = 1
)

# Activate venv if present
if (Test-Path .venv) {
    . .\.venv\Scripts\Activate.ps1
}

# Run in background
Start-Process -FilePath python -ArgumentList "src\main.py --display-limit-seconds $displayLimitSeconds --poll-interval-seconds $pollIntervalSeconds" -WindowStyle Hidden
