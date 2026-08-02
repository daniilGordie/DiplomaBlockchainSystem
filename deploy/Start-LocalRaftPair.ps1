[CmdletBinding()]
param(
    [string]$DataRoot = "",
    [switch]$SkipBuild,
    [switch]$Clean,
    [switch]$UsePersistentMembership,
    [string]$ProducerKeyPath = "",
    [string]$ProducerPrivateKeyPassword = "local-raft-producer-password",
    [int]$KeepAliveSeconds = 0
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $scriptRoot "..\\.tmp\\local-raft-pair"
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
$nodeProject = Join-Path $repoRoot "Blockchain.Node\\Blockchain.Node.csproj"
$nodeDll = Join-Path $repoRoot "Blockchain.Node\\bin\\Debug\\net8.0\\Blockchain.Node.dll"
$dataRootFull = [System.IO.Path]::GetFullPath($DataRoot)
$tmpRootFull = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ".tmp"))

if (-not $SkipBuild) {
    dotnet build $nodeProject | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path $nodeDll)) {
    throw "Node assembly not found at $nodeDll. Build Blockchain.Node first."
}

if ($Clean -and (Test-Path $dataRootFull)) {
    if (-not $dataRootFull.StartsWith($tmpRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean DataRoot outside repository .tmp directory: $dataRootFull"
    }

    Remove-Item -LiteralPath $dataRootFull -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $dataRootFull | Out-Null

$sharedSecrets = @{
    NodeDbPassword = "local-raft-db-password"
    NodeAdminToken = "local-raft-admin-token"
    OraclePrivateKeyPassword = "local-raft-oracle-password"
    OraclePublicKey = "auto"
    WebhookSecret = "local-raft-webhook-secret"
    OracleKeyPath = (Join-Path $dataRootFull "shared\\oracle_key.dat")
    Consensus__ProducerPrivateKeyPassword = $ProducerPrivateKeyPassword
    P2P__NodeRole = "Full"
    P2P__RegistrationToken = "local-raft-registration-token"
    P2P__DiscoveryIntervalSeconds = "3600"
    P2P__AllowRegistrationTokenFallback = "false"
    P2P__Iroh__Enabled = "false"
}

if (-not [string]::IsNullOrWhiteSpace($ProducerKeyPath)) {
    $sharedSecrets["Consensus__ProducerKeyPath"] = [System.IO.Path]::GetFullPath($ProducerKeyPath)
}

$nodes = @(
    @{
        Name = "node-a"
        HttpUrl = "http://localhost:7042"
        PublicUrl = "http://localhost:7042"
        RaftUrl = "http://localhost:6041"
        PeerRaftId = "node-b"
        PeerRaftUrl = "http://localhost:6042"
    },
    @{
        Name = "node-b"
        HttpUrl = "http://localhost:7043"
        PublicUrl = "http://localhost:7043"
        RaftUrl = "http://localhost:6042"
        PeerRaftId = "node-a"
        PeerRaftUrl = "http://localhost:6041"
    }
)

function Start-LocalNode {
    param(
        [hashtable]$Node,
        [hashtable]$SharedSecrets,
        [string]$Root,
        [string]$DllPath
    )

    $nodeRoot = Join-Path $Root $Node.Name
    New-Item -ItemType Directory -Force -Path $nodeRoot | Out-Null

    $envMap = [ordered]@{
        ASPNETCORE_URLS = $Node.HttpUrl
        ConnectionStrings__DefaultNodeDb = (Join-Path $nodeRoot "node.db")
        P2P__NodeId = $Node.Name
        P2P__PublicUrl = $Node.PublicUrl
        P2P__IdentityKeyPath = (Join-Path $nodeRoot "node-identity.p256.key")
        Raft__NodeId = $Node.Name
        Raft__PublicEndPoint = $Node.RaftUrl
        Raft__LogPath = (Join-Path $nodeRoot "raft-log")
        Raft__UsePersistentMembership = if ($UsePersistentMembership) { "true" } else { "false" }
        Raft__MembershipPath = (Join-Path $nodeRoot "raft-membership")
        Raft__SnapshotPath = (Join-Path $nodeRoot "raft-snapshots")
        Raft__Peers__0__Id = $Node.PeerRaftId
        Raft__Peers__0__EndPoint = $Node.PeerRaftUrl
    }

    foreach ($entry in $SharedSecrets.GetEnumerator()) {
        $envMap[$entry.Key] = $entry.Value
    }

    $logPath = Join-Path $nodeRoot "node.log"
    $errPath = Join-Path $nodeRoot "node.err.log"
    $pidPath = Join-Path $nodeRoot "node.pid"

    Remove-Item -LiteralPath $logPath, $errPath -Force -ErrorAction SilentlyContinue

    $previousValues = @{}
    foreach ($entry in $envMap.GetEnumerator()) {
        $previousValues[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, "Process")
    }

    try {
        $process = Start-Process -FilePath "dotnet" `
            -ArgumentList @($DllPath) `
            -WorkingDirectory $repoRoot `
            -WindowStyle Hidden `
            -RedirectStandardOutput $logPath `
            -RedirectStandardError $errPath `
            -PassThru
    }
    finally {
        foreach ($entry in $previousValues.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
        }
    }

    Start-Sleep -Milliseconds 750
    if ($process.HasExited) {
        throw "Node $($Node.Name) exited immediately. Check $logPath and $errPath."
    }

    Set-Content -Path $pidPath -Value $process.Id
    return [pscustomobject]@{
        Name = $Node.Name
        HttpUrl = $Node.HttpUrl
        RaftUrl = $Node.RaftUrl
        ProcessId = $process.Id
        NodeRoot = $nodeRoot
        LogPath = $logPath
        ErrorLogPath = $errPath
    }
}

function Wait-ForEndpoint {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri $Url -TimeoutSec 3 | Out-Null
            return $true
        }
        catch {
            Start-Sleep -Seconds 1
        }
    }

    return $false
}

function Assert-RaftConverged {
    param(
        [object[]]$Snapshots
    )

    $leaders = @($Snapshots | Where-Object { $_.localNodeIsLeader -eq $true })
    if ($leaders.Count -ne 1) {
        throw "Expected exactly one local Raft leader, got $($leaders.Count)."
    }

    $leaderEndpoint = [string]$leaders[0].leader
    if ([string]::IsNullOrWhiteSpace($leaderEndpoint)) {
        throw "The elected local Raft leader does not report its leader endpoint."
    }

    foreach ($status in $Snapshots) {
        if ([string]$status.leader -ne $leaderEndpoint) {
            throw "Raft nodes did not converge on one leader. Expected $leaderEndpoint, node $($status.configuration.nodeId) sees $($status.leader)."
        }
    }
}

$startedNodes = foreach ($node in $nodes) {
    Start-LocalNode -Node $node -SharedSecrets $sharedSecrets -Root $dataRootFull -DllPath $nodeDll
}

foreach ($node in $startedNodes) {
    if (-not (Wait-ForEndpoint -Url "$($node.HttpUrl)/healthz")) {
        throw "Node $($node.Name) did not become healthy. Check $($node.LogPath) and $($node.ErrorLogPath)."
    }
}

$statusSnapshots = foreach ($node in $startedNodes) {
    Invoke-RestMethod -Uri "$($node.HttpUrl)/api/consensus/raft/status" -TimeoutSec 5
}

Write-Host "Local Raft pair started:"
foreach ($node in $startedNodes) {
    Write-Host "  $($node.Name): $($node.HttpUrl)  raft=$($node.RaftUrl)  pid=$($node.ProcessId)"
}

Write-Host ""
Write-Host "Raft status:"
foreach ($status in $statusSnapshots) {
    $leader = if ($null -eq $status.leader) { "<none>" } else { [string]$status.leader }
    $term = if ($null -eq $status.term) { "<null>" } else { [string]$status.term }
    Write-Host ("  node={0} ready={1} leader={2} term={3} localLeader={4}" -f `
        $status.configuration.nodeId,
        $status.configuration.ready,
        $leader,
        $term,
        $status.localNodeIsLeader)
}

if ($KeepAliveSeconds -gt 0) {
    Write-Host ""
    Write-Host "Keeping local Raft pair alive for $KeepAliveSeconds seconds."
    Start-Sleep -Seconds $KeepAliveSeconds

    $finalSnapshots = foreach ($node in $startedNodes) {
        Invoke-RestMethod -Uri "$($node.HttpUrl)/api/consensus/raft/status" -TimeoutSec 5
    }

    Write-Host ""
    Write-Host "Raft status after wait:"
    foreach ($status in $finalSnapshots) {
        $leader = if ($null -eq $status.leader) { "<none>" } else { [string]$status.leader }
        $term = if ($null -eq $status.term) { "<null>" } else { [string]$status.term }
        Write-Host ("  node={0} ready={1} leader={2} term={3} localLeader={4}" -f `
            $status.configuration.nodeId,
            $status.configuration.ready,
            $leader,
            $term,
            $status.localNodeIsLeader)
    }

    Assert-RaftConverged -Snapshots $finalSnapshots
}
