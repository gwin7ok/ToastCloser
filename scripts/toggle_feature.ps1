# Toggle ToastCloser disable state via Named Pipe
# Usage:
#   .\toggle_feature.ps1 toggle
#   .\toggle_feature.ps1 disable
#   .\toggle_feature.ps1 enable
param(
    [Parameter(Mandatory=$true)][string]$Command
)
$pipeName = "\\.\pipe\ToastCloserControl"
try {
    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ToastCloserControl', [System.IO.Pipes.PipeDirection]::InOut)
    $client.Connect(2000)
    # Use UTF8 without BOM to avoid sending a BOM character at stream start
    $enc = New-Object System.Text.UTF8Encoding($false)
    $sw = New-Object System.IO.StreamWriter($client, $enc)
    $sw.AutoFlush = $true
    $sr = New-Object System.IO.StreamReader($client, $enc)
    $sw.WriteLine($Command)
    $resp = $sr.ReadLine()
    Write-Host "Response: $resp"
    try { $sr.Dispose() } catch { }
    try { $sw.Dispose() } catch { }
    try { $client.Dispose() } catch { }
} catch {
    Write-Error "Failed to connect to named pipe: $_"
}
