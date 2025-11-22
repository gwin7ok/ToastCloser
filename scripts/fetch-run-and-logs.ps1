param(
    [string]$Repo = 'gwin7ok/noticeWindowFinder',
    [string]$Workflow = 'release.yml',
    [int]$TimeoutSeconds = 300,
    [int]$PollIntervalSeconds = 5
)

Write-Host "fetch-run-and-logs.ps1: repo=$Repo workflow=$Workflow timeout=${TimeoutSeconds}s poll=${PollIntervalSeconds}s"

$start = Get-Date
$runId = $null
while ($true) {
    try {
        gh run list --repo $Repo --workflow $Workflow --limit 1 --json databaseId,conclusion,createdAt | Out-File -FilePath runlist.json -Encoding utf8
        $raw = Get-Content runlist.json -Raw
        if (-not $raw) { Write-Host 'gh returned empty; sleeping'; Start-Sleep -Seconds $PollIntervalSeconds; continue }
        $run = $raw | ConvertFrom-Json | Select-Object -First 1
        if ($null -eq $run) { Write-Host 'No run object; sleeping'; Start-Sleep -Seconds $PollIntervalSeconds; continue }
        $runId = $run.databaseId
        $conclusion = $run.conclusion
        Write-Host "Found run id=$runId conclusion='$conclusion' createdAt=$($run.createdAt)"
        if ($conclusion -and $conclusion -ne '') { break }
    } catch {
        Write-Host "Error listing runs: $_"
    }
    if ( ((Get-Date) - $start).TotalSeconds -gt $TimeoutSeconds ) {
        Write-Host 'Timeout waiting for run to complete'
        break
    }
    Start-Sleep -Seconds $PollIntervalSeconds
}

if (-not $runId) { Write-Host 'No run id found; exiting with code 2'; exit 2 }

$logFile = Join-Path $PWD ("run-$runId.log")
Write-Host "Downloading logs for run $runId -> $logFile"
try {
    gh run view $runId --repo $Repo --log --exit-status | Tee-Object -FilePath $logFile
    Write-Host "Saved logs to: $logFile"
} catch {
    Write-Host "Failed to fetch logs: $_"
    exit 3
}

try {
    gh run view $runId --repo $Repo --json conclusion,htmlUrl | ConvertFrom-Json | Format-List
} catch {
    Write-Host "Could not get run metadata: $_"
}

exit 0
