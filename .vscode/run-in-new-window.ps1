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
  $appArgs = @('10','30','5')
}

# Build the command to run inside the new PowerShell window
# Use single quotes around the project path to avoid nested-quote parsing issues
$dotnetCmd = "dotnet run --project '$projectPath' -- $($appArgs -join ' ')"

# Diagnostic log for invocation
$diagLog = Join-Path $workspaceFolder '.vscode\run-in-new-window.log'
"$(Get-Date -Format o) INVOKE: workspace=$workspaceFolder keepOpen=$keepOpen appArgs=$($appArgs -join ',') dotnetCmd=$dotnetCmd" | Out-File -FilePath $diagLog -Encoding utf8 -Append

# Start a new PowerShell window and run the command.
if ($keepOpenBool) {
  # Use cmd.exe "start" to reliably open a new console window and keep it open with -NoExit
  $startArgs = @('/c', 'start', 'pwsh', '-NoProfile', '-NoExit', '-Command', $dotnetCmd)
  Start-Process -FilePath 'cmd.exe' -ArgumentList $startArgs -WindowStyle Normal
} else {
  # Start pwsh without -NoExit (window will close when command completes)
  $startArgs = @('-NoProfile', '-Command', $dotnetCmd)
  Start-Process -FilePath 'pwsh' -ArgumentList $startArgs -WindowStyle Normal
}
