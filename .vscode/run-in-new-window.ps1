param(
  [Parameter(Mandatory=$true)]
  [string]$workspaceFolder,
  [Parameter(Mandatory=$false)]
  [string]$keepOpen = 'true',
  [Parameter(Mandatory=$false, ValueFromRemainingArguments=$true)]
  [string[]]$appArgs
)

# Path to the project (adjust if your project layout differs)
$projectPath = Join-Path $workspaceFolder 'csharp\ToastCloser\ToastCloser.csproj'

# Normalize keepOpen to boolean (accept strings like 'true','false','1','0')
$keepOpenBool = $false
switch -Regex ($keepOpen.ToString().ToLower()) {
  '^(true|1|yes|y)$' { $keepOpenBool = $true; break }
  default { $keepOpenBool = $false }
}

# Determine application arguments (default: 10 30 1)
if ($null -eq $appArgs -or $appArgs.Length -eq 0) {
  # default named options
  $appArgs = @('--display-limit-seconds=10','--poll-interval-seconds=5')
}

# Enforce named options only: reject any positional (non --) args
foreach ($a in $appArgs) {
  if (-not ($a -like '--*')) {
    Write-Error "Positional arguments are no longer supported. Use named options like --display-limit-seconds= and --poll-interval-seconds=. Received: $a"
    exit 2
  }
}

# Build the command to run inside the new PowerShell window
# Use single quotes around the project path to avoid nested-quote parsing issues
$dotnetCmd = "dotnet run --project '$projectPath' -- $($appArgs -join ' ')"

# Diagnostic log for invocation
$diagLog = Join-Path $workspaceFolder '.vscode\run-in-new-window.log'
"$(Get-Date -Format o) INVOKE: workspace=$workspaceFolder keepOpen=$keepOpen appArgs=$($appArgs -join ',') dotnetCmd=$dotnetCmd" | Out-File -FilePath $diagLog -Encoding utf8 -Append

# Start a new PowerShell window and run the command.
if ($keepOpenBool) {
  # Start pwsh directly in a new window and keep it open (-NoExit).
  # Using Start-Process directly avoids an intermediate cmd.exe start which
  # can introduce transient initialization/race issues on some systems.
  $pwshArgs = @('-NoProfile', '-NoExit', '-Command', $dotnetCmd)
  Start-Process -FilePath 'pwsh' -ArgumentList $pwshArgs -WindowStyle Normal
} else {
  # Start pwsh without -NoExit (window will close when command completes)
  $pwshArgs = @('-NoProfile', '-Command', $dotnetCmd)
  Start-Process -FilePath 'pwsh' -ArgumentList $pwshArgs -WindowStyle Normal
}
