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
$previousRelayMode = $env:NEXUS_IROH_RELAY_MODE
$statusUrls = @(
    "http://localhost:7452/api/consensus/raft/status",
    "http://localhost:7453/api/consensus/raft/status",
    "http://localhost:7454/api/consensus/raft/status"
)

function Read-Json([string]$Url) {
    Invoke-RestMethod -Uri $Url -TimeoutSec 15
}

try {
    if (-not $SkipClusterStart) {
        $env:NEXUS_IROH_RELAY_MODE = "relay-only"
        & $productSmoke -RaftTransport Iroh -SkipBuild -KeepContainersOnFailure -TimeoutSeconds $TimeoutSeconds
        if ($LASTEXITCODE -ne 0) {
            throw "Iroh product smoke failed before relay verification."
        }
    }

    $statuses = @($statusUrls | ForEach-Object { Read-Json $_ })
    foreach ($status in $statuses) {
        if ($status.configuration.transport -ne "Iroh") {
            throw "Node $($status.configuration.nodeId) is not using Iroh Raft transport."
        }

        if (($status.configuration.publicEndPoint ?? "") -ne "") {
            throw "Node $($status.configuration.nodeId) exposes TCP Raft public endpoint in Iroh relay smoke."
        }

        if (($status.transportProof.connectionPath ?? "") -ne "relay") {
            throw "Node $($status.configuration.nodeId) did not prove relay path. Current path: $($status.transportProof.connectionPath)"
        }

        if (($status.transportProof.irohTransportMode ?? "") -ne "RelayOnly") {
            throw "Node $($status.configuration.nodeId) is not in RelayOnly mode. Current mode: $($status.transportProof.irohTransportMode)"
        }

        if (($status.transportProof.directAddressCount ?? 0) -ne 0) {
            throw "Node $($status.configuration.nodeId) still exposes direct Iroh addresses in RelayOnly mode."
        }

        if (($status.transportProof.alpn ?? "") -ne "nexus/raft/1") {
            throw "Node $($status.configuration.nodeId) does not report the Raft ALPN."
        }
    }

    Write-Host "Nexus relay smoke passed."
    exit 0
}
catch {
    Write-Host "Nexus relay smoke failed: $($_.Exception.Message)"
    if (-not $KeepContainersOnFailure) {
        docker compose -f $composeFile down --remove-orphans | Out-Host
    }
    exit 1
}
finally {
    $env:NEXUS_IROH_RELAY_MODE = $previousRelayMode
    if (-not $KeepContainersOnFailure -and -not $SkipClusterStart) {
        docker compose -f $composeFile down --remove-orphans | Out-Host
    }
}
