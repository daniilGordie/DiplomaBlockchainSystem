[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 600,
    [switch]$SkipClusterStart,
    [switch]$KeepContainersOnFailure
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$productSmoke = Join-Path $scriptRoot "Invoke-NexusProductSmoke.ps1"
$composeFile = Join-Path $scriptRoot "docker-compose.iroh-raft-smoke.yml"
$statusUrls = @(
    "http://localhost:7452/api/consensus/raft/status",
    "http://localhost:7453/api/consensus/raft/status",
    "http://localhost:7454/api/consensus/raft/status"
)
$snapshotUrls = @(
    "http://localhost:7452/api/consensus/raft/snapshot",
    "http://localhost:7453/api/consensus/raft/snapshot",
    "http://localhost:7454/api/consensus/raft/snapshot"
)
$adminToken = "local-raft-admin-token"

function Read-Json([string]$Url) {
    Invoke-RestMethod -Uri $Url -TimeoutSec 15
}

function Assert-Snapshot([object]$Status, [string]$Url) {
    if (-not $Status.operational) {
        throw "$Url is not operational: $($Status.operationalStatus)."
    }

    if ($Status.configuration.transport -ne "Iroh") {
        throw "$Url is not using Raft transport Iroh."
    }

    if ($null -eq $Status.snapshot) {
        throw "$Url did not expose snapshot diagnostics."
    }

    if ($null -eq $Status.snapshot.publishedSnapshotIndex) {
        throw "$Url has no published DotNext snapshot. Last applied index: $($Status.snapshot.lastAppliedIndex)."
    }
}

function Invoke-SnapshotTrigger([string]$Url) {
    try {
        Invoke-RestMethod -Method Post -Uri $Url -ContentType "application/json" -Body (@{ adminToken = $adminToken } | ConvertTo-Json) -TimeoutSec 30
    }
    catch {
        return $null
    }
}

try {
    if (-not $SkipClusterStart) {
        & $productSmoke -RaftTransport Iroh -SkipBuild -KeepContainersOnFailure -TimeoutSeconds $TimeoutSeconds
        if ($LASTEXITCODE -ne 0) {
            throw "Iroh product smoke failed before snapshot verification."
        }
    }

    foreach ($url in $snapshotUrls) {
        Invoke-SnapshotTrigger $url | Out-Null
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        try {
            $statuses = @($statusUrls | ForEach-Object { Read-Json $_ })
            $last = $statuses
            if (($statuses | Where-Object { $null -ne $_.snapshot.publishedSnapshotIndex }).Count -eq $statuses.Count) {
                foreach ($i in 0..($statuses.Count - 1)) {
                    Assert-Snapshot $statuses[$i] $statusUrls[$i]
                }

                Write-Host "Iroh Raft snapshot smoke passed."
                exit 0
            }
        }
        catch {
            $last = $_.Exception.Message
        }

        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    throw "DotNext snapshot was not created before timeout. Last status: $($last | ConvertTo-Json -Depth 8)"
}
catch {
    Write-Host "Iroh Raft snapshot smoke failed: $($_.Exception.Message)"
    if (-not $KeepContainersOnFailure) {
        docker compose -f $composeFile down --remove-orphans | Out-Host
    }
    exit 1
}
finally {
    if (-not $KeepContainersOnFailure -and -not $SkipClusterStart) {
        docker compose -f $composeFile down --remove-orphans | Out-Host
    }
}
