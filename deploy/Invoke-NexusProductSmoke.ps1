[CmdletBinding()]
param(
    [ValidateSet("Tcp", "Iroh")]
    [string]$RaftTransport = "Tcp",
    [switch]$SkipBuild,
    [switch]$KeepContainersOnFailure,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
$dataRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ".tmp\nexus-product-smoke"))
$raftDataRoot = Join-Path $dataRoot "raft"
$setupProject = Join-Path $repoRoot "Nexus.Setup\Nexus.Setup.csproj"
$smokeProject = Join-Path $repoRoot "tools\RaftGrpcSmoke\RaftGrpcSmoke.csproj"
$composeFile = Join-Path $scriptRoot "docker-compose.raft-smoke.yml"
$irohComposeFile = Join-Path $scriptRoot "docker-compose.iroh-raft-smoke.yml"
$adminToken = "local-raft-admin-token"
$previousRaftSmokeDataRoot = $env:RAFT_SMOKE_DATA_ROOT
$previousIrohRaftSmokeDataRoot = $env:IROH_RAFT_SMOKE_DATA_ROOT
$previousNodeAIrohId = $env:NODE_A_IROH_ID
$previousNodeBIrohId = $env:NODE_B_IROH_ID
$previousNodeCIrohId = $env:NODE_C_IROH_ID
$env:RAFT_SMOKE_DATA_ROOT = $raftDataRoot

function Step([string]$Name, [scriptblock]$Action) {
    Write-Host ""
    Write-Host "== $Name =="
    try {
        & $Action
    }
    catch {
        Write-Host "FAILED: $Name"
        throw
    }
}

function Require-HttpJson([string]$Url) {
    $result = Invoke-RestMethod -Uri $Url -TimeoutSec 10
    if ($null -eq $result) {
        throw "No JSON returned from $Url"
    }
    return $result
}

function Start-SetupNode([string]$Root, [int]$Port) {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $nodeProject = Join-Path $repoRoot "Blockchain.Node\Blockchain.Node.csproj"
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    $psi.WorkingDirectory = $Root
    $psi.UseShellExecute = $false
    $psi.ArgumentList.Add("run")
    $psi.ArgumentList.Add("--no-build")
    $psi.ArgumentList.Add("--no-launch-profile")
    $psi.ArgumentList.Add("--project")
    $psi.ArgumentList.Add($nodeProject)
    $psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:$Port"
    $psi.Environment["NEXUS_SETUP_CONFIG_PATH"] = (Join-Path $Root "nexus.setup.json")
    $psi.Environment["NEXUS_DATA_PATH"] = (Join-Path $Root "data")
    foreach ($key in @(
        "OraclePublicKey",
        "WebhookSecret",
        "NodeDbPassword",
        "NodeAdminToken",
        "ORACLE_PUBLIC_KEY",
        "WEBHOOK_SECRET",
        "NODE_DB_PASSWORD",
        "NODE_ADMIN_TOKEN")) {
        if ($psi.Environment.ContainsKey($key)) {
            $psi.Environment.Remove($key) | Out-Null
        }
    }

    $process = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $process) { throw "Failed to start setup node on port $Port." }
    return $process
}

function Stop-SetupNode($Process) {
    if ($null -ne $Process -and -not $Process.HasExited) {
        $Process.Kill($true)
        $Process.WaitForExit(10000) | Out-Null
    }
}

function Wait-HttpJson([string]$Url, [int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        try {
            return Require-HttpJson $Url
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    } while ((Get-Date) -lt $deadline)

    return Require-HttpJson $Url
}

function Invoke-InAppSetupSmoke {
    $setupRoot = Join-Path $dataRoot "in-app-setup"
    $createRoot = Join-Path $setupRoot "create-network"
    $joinRoot = Join-Path $setupRoot "join-network"
    $localRoot = Join-Path $setupRoot "local-node"
    $restoreRoot = Join-Path $setupRoot "restore-node"
    $createPort = 7561
    $joinPort = 7562
    $localPort = 7563
    $restorePort = 7564
    $createProcess = $null
    $joinProcess = $null
    $localProcess = $null
    $restoreProcess = $null
    $script:setupSmokeInviteText = ""
    $script:setupSmokeBackupPassword = "setup-smoke-backup-password"
    $script:setupSmokeBackupPath = Join-Path $setupRoot "local-node.nexus-backup"
    $script:setupSmokeLocalNodeId = ""
    $script:setupSmokeLocalFingerprint = ""

    Step "In-app setup smoke: Create network" {
        try {
            $createProcess = Start-SetupNode $createRoot $createPort
            $state = Wait-HttpJson "http://127.0.0.1:$createPort/api/setup/state"
            if ($state.state -ne "NotConfigured") { throw "Expected NotConfigured, got $($state.state)." }
            $body = @{
                networkName = "Nexus Setup Smoke"
                mode = "all-in-one"
                networkId = "setup-smoke-network"
                nodeId = "setup-smoke-bootstrap"
                publicHttpUrl = "http://127.0.0.1:$createPort"
                publicGrpcUrl = "http://127.0.0.1:$createPort"
                raftTransport = "Iroh"
                raftPublicEndPoint = ""
                irohNodeId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                bootstrapIrohUrl = "iroh://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            } | ConvertTo-Json
            $created = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$createPort/api/setup/create-network" -ContentType "application/json" -Body $body -TimeoutSec 20
            if (-not $created.success) { throw "Create network failed: $($created.message)" }
            if ([string]::IsNullOrWhiteSpace([string]$created.invite.networkId)) { throw "Create network did not return an invite." }
            if ($created.configuration.'Raft:PublicEndPoint') { throw "Iroh setup unexpectedly required a TCP Raft endpoint." }
            $script:setupSmokeInviteText = $created.invite | ConvertTo-Json -Depth 10 -Compress
        }
        finally {
            Stop-SetupNode $createProcess
        }
    }

    Step "In-app setup smoke: Join network" {
        try {
            $joinProcess = Start-SetupNode $joinRoot $joinPort
            Wait-HttpJson "http://127.0.0.1:$joinPort/api/setup/state" | Out-Null
            $inviteBody = @{ invite = $script:setupSmokeInviteText } | ConvertTo-Json -Depth 10
            $validation = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$joinPort/api/setup/validate-invite" -ContentType "application/json" -Body $inviteBody -TimeoutSec 20
            if (-not $validation.valid) { throw "Validation rejected create-network invite: $($validation.message)" }
            $joinBody = @{
                invite = $script:setupSmokeInviteText
                role = "Edge"
                nodeId = "setup-smoke-edge"
            } | ConvertTo-Json -Depth 10
            $joined = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$joinPort/api/setup/join-network" -ContentType "application/json" -Body $joinBody -TimeoutSec 20
            if (-not $joined.success -or $joined.nextState -ne "AwaitingApproval") { throw "Join network failed: $($joined.message)" }
            if ($joined.configuration.'Raft:NodeId') { throw "Edge join received Raft member configuration." }
        }
        finally {
            Stop-SetupNode $joinProcess
        }
    }

    Step "In-app setup smoke: Local private node restart" {
        try {
            $localProcess = Start-SetupNode $localRoot $localPort
            Wait-HttpJson "http://127.0.0.1:$localPort/api/setup/state" | Out-Null
            $localBody = @{
                networkName = "Local setup smoke"
                networkId = "local-setup-smoke"
                nodeId = "local-setup-node"
            } | ConvertTo-Json
            $local = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$localPort/api/setup/local" -ContentType "application/json" -Body $localBody -TimeoutSec 20
            if (-not $local.success) { throw "Local setup failed: $($local.message)" }
            Stop-SetupNode $localProcess
            $localProcess = Start-SetupNode $localRoot $localPort
            $health = Wait-HttpJson "http://127.0.0.1:$localPort/healthz" 90
            if ($health.status -ne "ok") { throw "Local node did not restart as configured." }
            $ready = Wait-HttpJson "http://127.0.0.1:$localPort/api/setup/state"
            if ($ready.state -ne "Ready") { throw "Expected Ready after restart, got $($ready.state)." }
            $localNetwork = Wait-HttpJson "http://127.0.0.1:$localPort/api/network/status"
            $script:setupSmokeLocalNodeId = $ready.nodeId
            $script:setupSmokeLocalFingerprint = $localNetwork.nodeIdentityFingerprint
            Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:$localPort/api/setup/backup" -Body @{ password = $script:setupSmokeBackupPassword } -OutFile $script:setupSmokeBackupPath -TimeoutSec 20 | Out-Null
            if (-not (Test-Path $script:setupSmokeBackupPath)) { throw "Local node backup was not created." }
        }
        finally {
            Stop-SetupNode $localProcess
        }
    }

    Step "In-app setup smoke: Restore activation" {
        try {
            $restoreProcess = Start-SetupNode $restoreRoot $restorePort
            Wait-HttpJson "http://127.0.0.1:$restorePort/api/setup/state" | Out-Null
            try {
                Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$restorePort/api/setup/restore" -Form @{ backup = Get-Item $script:setupSmokeBackupPath; password = "wrong-password" } -TimeoutSec 20 | Out-Null
                throw "Restore accepted an incorrect backup password."
            }
            catch {
                if ($_.Exception.Response.StatusCode.value__ -ne 400) { throw }
            }

            $restore = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$restorePort/api/setup/restore" -Form @{ backup = Get-Item $script:setupSmokeBackupPath; password = $script:setupSmokeBackupPassword } -TimeoutSec 20
            if (-not $restore.success -or $restore.nextState -ne "AwaitingRestart") { throw "Restore activation failed: $($restore.message)" }
            Stop-SetupNode $restoreProcess
            $restoreProcess = Start-SetupNode $restoreRoot $restorePort
            $restoredState = Wait-HttpJson "http://127.0.0.1:$restorePort/api/setup/state" 90
            if ($restoredState.state -ne "Ready") { throw "Restored node did not start as Ready." }
            if ($restoredState.nodeId -ne $script:setupSmokeLocalNodeId) { throw "Restored node id changed." }
            $restoredNetwork = Wait-HttpJson "http://127.0.0.1:$restorePort/api/network/status"
            if ($restoredNetwork.nodeIdentityFingerprint -ne $script:setupSmokeLocalFingerprint) { throw "Restored fingerprint changed." }
        }
        finally {
            Stop-SetupNode $restoreProcess
        }
    }
}

function Invoke-IrohRaftSmoke {
    $irohDataRoot = Join-Path $dataRoot "iroh-raft"
    $producerKeyPath = Join-Path $irohDataRoot "shared\producer-key.dat"
    $smokeImage = "nexus-iroh-sidecar-smoke:latest"
    $env:IROH_RAFT_SMOKE_DATA_ROOT = $irohDataRoot

    Step "Prepare Iroh Raft seed" {
        New-Item -ItemType Directory -Force -Path (Join-Path $irohDataRoot "iroh") | Out-Null
        dotnet run --no-build --project $smokeProject -- prepare `
            --data-root $irohDataRoot `
            --nodes "node-a,node-b,node-c" `
            --producer-key-path $producerKeyPath `
            --producer-password "local-raft-producer-password" `
            --db-password "local-raft-db-password" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Iroh Raft prepare failed." }
    }

    Step "Build Iroh sidecar image" {
        $sidecarDockerfile = Join-Path $repoRoot "iroh-sidecar\Dockerfile"
        docker build -f $sidecarDockerfile -t $smokeImage $repoRoot | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Iroh sidecar image build failed." }
    }

    Step "Generate Iroh member identities" {
        $ids = @{}
        $irohIdentityRoot = Join-Path $irohDataRoot "iroh"
        foreach ($name in @("node-a", "node-b", "node-c")) {
            $keyPath = "/data/$name.key"
            $idOutput = docker run --rm -v "${irohIdentityRoot}:/data" $smokeImage --secret-key-path $keyPath --print-node-id
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($idOutput)) { throw "Failed to generate Iroh id for $name." }
            $idText = ($idOutput -join "`n")
            $match = [regex]::Match($idText, "(?m)^[0-9a-fA-F]{64}$")
            if (-not $match.Success) {
                throw "Failed to parse Iroh id for $name. Output: $idText"
            }
            $ids[$name] = $match.Value.ToLowerInvariant()
        }

        $env:NODE_A_IROH_ID = $ids["node-a"]
        $env:NODE_B_IROH_ID = $ids["node-b"]
        $env:NODE_C_IROH_ID = $ids["node-c"]
        Write-Host "node-a iroh://$($env:NODE_A_IROH_ID)"
        Write-Host "node-b iroh://$($env:NODE_B_IROH_ID)"
        Write-Host "node-c iroh://$($env:NODE_C_IROH_ID)"
    }

    Step "Start 3-node Raft-over-Iroh stack" {
        docker compose -f $irohComposeFile down --remove-orphans | Out-Host
        if ($SkipBuild) {
            docker compose -f $irohComposeFile up -d --no-build | Out-Host
        }
        else {
            docker compose -f $irohComposeFile up -d --build | Out-Host
        }
        if ($LASTEXITCODE -ne 0) { throw "Iroh Raft compose up failed." }
    }

    Step "Wait for Iroh Raft election" {
        $a = $null
        $b = $null
        $c = $null
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        do {
            try {
                $a = Require-HttpJson "http://localhost:7452/api/consensus/raft/status"
                $b = Require-HttpJson "http://localhost:7453/api/consensus/raft/status"
                $c = Require-HttpJson "http://localhost:7454/api/consensus/raft/status"
                if ($a.operational -and $b.operational -and $c.operational -and
                    -not [string]::IsNullOrWhiteSpace([string]$a.leader) -and
                    [string]$a.leader -eq [string]$b.leader -and
                    [string]$a.leader -eq [string]$c.leader) {
                    break
                }
            }
            catch {
                Start-Sleep -Seconds 2
            }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $deadline)

        if ($null -eq $a -or $null -eq $b -or $null -eq $c -or
            -not $a.operational -or -not $b.operational -or -not $c.operational -or
            [string]::IsNullOrWhiteSpace([string]$a.leader) -or
            [string]$a.leader -ne [string]$b.leader -or
            [string]$a.leader -ne [string]$c.leader) {
            throw "Iroh Raft leader was not elected before timeout."
        }
    }

    Step "Commit signed intent over Iroh Raft" {
        dotnet run --no-build --project $smokeProject -- create-project `
            --submit-url "http://localhost:7452" `
            --verify-urls "http://localhost:7452,http://localhost:7453,http://localhost:7454" `
            --project-id "IrohSmoke_$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))" `
            --user "IrohSmokeUser" `
            --timeout-seconds $TimeoutSeconds `
            --duplicate-check | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Iroh Raft signed intent commit failed." }
    }

    Step "Restart Iroh Raft leader candidates and verify recovery" {
        docker compose -f $irohComposeFile restart node-a node-b node-c sidecar-a sidecar-b sidecar-c | Out-Host
        Start-Sleep -Seconds 10
        $a = $null
        $b = $null
        $c = $null
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        do {
            try {
                $a = Require-HttpJson "http://localhost:7452/api/consensus/raft/status"
                $b = Require-HttpJson "http://localhost:7453/api/consensus/raft/status"
                $c = Require-HttpJson "http://localhost:7454/api/consensus/raft/status"
                if ($a.operational -and $b.operational -and $c.operational -and
                    -not [string]::IsNullOrWhiteSpace([string]$a.leader) -and
                    [string]$a.leader -eq [string]$b.leader -and
                    [string]$a.leader -eq [string]$c.leader) {
                    break
                }
            }
            catch {
            }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $deadline)

        if ($null -eq $a -or $null -eq $b -or $null -eq $c -or
            -not $a.operational -or -not $b.operational -or -not $c.operational -or
            [string]::IsNullOrWhiteSpace([string]$a.leader) -or
            [string]$a.leader -ne [string]$b.leader -or
            [string]$a.leader -ne [string]$c.leader) {
            throw "Iroh Raft did not recover to an operational state after restart."
        }
    }
}

if (Test-Path $dataRoot) {
    Remove-Item -LiteralPath $dataRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

try {
    Step "Build smoke tools" {
        if (-not $SkipBuild) {
            dotnet build $smokeProject | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "RaftGrpcSmoke build failed." }
            dotnet build $setupProject | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Nexus.Setup build failed." }
        }
    }

    Invoke-InAppSetupSmoke

    if ($RaftTransport -eq "Iroh") {
        Invoke-IrohRaftSmoke
        Write-Host ""
        Write-Host "Nexus product smoke passed."
        if (-not $KeepContainersOnFailure) {
            docker compose -f $irohComposeFile down --remove-orphans | Out-Host
        }
        $env:RAFT_SMOKE_DATA_ROOT = $previousRaftSmokeDataRoot
        $env:IROH_RAFT_SMOKE_DATA_ROOT = $previousIrohRaftSmokeDataRoot
        $env:NODE_A_IROH_ID = $previousNodeAIrohId
        $env:NODE_B_IROH_ID = $previousNodeBIrohId
        $env:NODE_C_IROH_ID = $previousNodeCIrohId
        exit 0
    }

    Step "Start Bootstrap/Consensus Raft stack" {
        & (Join-Path $scriptRoot "Invoke-DockerRaftSmoke.ps1") -DataRoot $raftDataRoot -SkipBuild -KeepRunning -WarmupSeconds 10 | Out-Host
    }

    Step "Verify diagnostics endpoints" {
        Require-HttpJson "http://localhost:7442/api/node/status" | Out-Null
        Require-HttpJson "http://localhost:7442/api/network/status" | Out-Null
        Require-HttpJson "http://localhost:7442/api/setup/diagnostics" | Out-Null
        $raft = Require-HttpJson "http://localhost:7442/api/consensus/raft/status"
        if (-not $raft.operational) { throw "Raft cluster is not operational: $($raft.operationalStatus)." }
        Require-HttpJson "http://localhost:7442/api/network/intents" | Out-Null
        Require-HttpJson "http://localhost:7442/api/network/membership" | Out-Null
    }

    Step "Submit and approve Edge membership" {
        $joinBody = @{
            networkId = "nexus-main"
            nodeId = "edge-smoke-1"
            nodePublicKey = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("edge-smoke-public-key"))
            irohUrl = "iroh://edge-smoke-1"
            irohNodeId = "edge-smoke-iroh"
            publicEndpoint = ""
            requestedRole = "Edge"
            appVersion = "smoke"
            protocolVersion = "1"
            capabilities = @("Edge", "Storage")
        } | ConvertTo-Json -Depth 5
        $join = Invoke-RestMethod -Method Post -Uri "http://localhost:7442/api/network/membership/join" -ContentType "application/json" -Body $joinBody -TimeoutSec 10
        if (-not $join.success) { throw "Join request failed: $($join.message)" }
        $pending = Require-HttpJson "http://localhost:7442/api/network/membership?status=PendingApproval"
        if (($pending.nodes | Where-Object { $_.nodeId -eq "edge-smoke-1" }).Count -eq 0) { throw "Pending Edge was not registered." }

        $approveBody = @{
            status = "Approved"
            adminToken = $adminToken
            actor = "product-smoke"
            reason = "smoke approval"
        } | ConvertTo-Json
        $approved = Invoke-RestMethod -Method Post -Uri "http://localhost:7442/api/network/membership/edge-smoke-1/status" -ContentType "application/json" -Body $approveBody -TimeoutSec 10
        if (-not $approved.success) { throw "Approval failed: $($approved.message)" }
    }

    Step "Create project through SignedIntent and duplicate-check idempotency" {
        dotnet run --no-build --project $smokeProject -- create-project `
            --submit-url "http://localhost:7442" `
            --verify-urls "http://localhost:7442,http://localhost:7443" `
            --project-id "ProductSmoke_$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))" `
            --user "ProductSmokeUser" `
            --timeout-seconds $TimeoutSeconds `
            --duplicate-check | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Signed intent duplicate-check failed." }
    }

    Step "Revoke and restore Edge membership" {
        $revokeBody = @{
            status = "Revoked"
            adminToken = $adminToken
            actor = "product-smoke"
            reason = "smoke revoke"
        } | ConvertTo-Json
        $revoked = Invoke-RestMethod -Method Post -Uri "http://localhost:7442/api/network/membership/edge-smoke-1/status" -ContentType "application/json" -Body $revokeBody -TimeoutSec 10
        if (-not $revoked.success) { throw "Revoke failed: $($revoked.message)" }
        $restoreBody = @{
            status = "Approved"
            adminToken = $adminToken
            actor = "product-smoke"
            reason = "smoke restore"
        } | ConvertTo-Json
        $restored = Invoke-RestMethod -Method Post -Uri "http://localhost:7442/api/network/membership/edge-smoke-1/status" -ContentType "application/json" -Body $restoreBody -TimeoutSec 10
        if (-not $restored.success) { throw "Restore approval failed: $($restored.message)" }
        $audit = Require-HttpJson "http://localhost:7442/api/network/membership/edge-smoke-1/audit"
        if ($audit.events.Count -lt 2) { throw "Membership audit did not record revoke/restore." }
    }

    Step "Setup backup and restore" {
        $envPath = Join-Path $dataRoot "edge.env"
        $restoreEnvPath = Join-Path $dataRoot "restored-edge.env"
        $edgeData = Join-Path $dataRoot "edge-data"
        $restoreData = Join-Path $dataRoot "restored-edge-data"
        New-Item -ItemType Directory -Force -Path $edgeData | Out-Null
        Set-Content -Path (Join-Path $edgeData "node-identity.p256.key") -Value "smoke-node-key"
        Set-Content -Path (Join-Path $edgeData "oracle_key.dat") -Value "smoke-oracle-key"
        Set-Content -Path (Join-Path $edgeData "producer-key.dat") -Value "smoke-producer-key"
        Set-Content -Path (Join-Path $edgeData "iroh-secret.key") -Value "smoke-iroh-key"
        dotnet run --no-build --project $setupProject -- setup edge --bootstrap http://localhost:7442 --bootstrap-http http://localhost:7442 --output $envPath --force | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "setup edge failed." }
        dotnet run --no-build --project $setupProject -- backup --env $envPath --data-root $edgeData --output (Join-Path $dataRoot "edge.nexus-backup") --password "product-smoke-backup-password" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "backup failed." }
        dotnet run --no-build --project $setupProject -- restore --input (Join-Path $dataRoot "edge.nexus-backup") --data-root $restoreData --env-output $restoreEnvPath --password "product-smoke-backup-password" --force | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "restore failed." }
        dotnet run --no-build --project $setupProject -- restore --input (Join-Path $dataRoot "edge.nexus-backup") --data-root (Join-Path $dataRoot "wrong") --env-output (Join-Path $dataRoot "wrong.env") --password "wrong-password" --force | Out-Null
        if ($LASTEXITCODE -eq 0) { throw "wrong backup password unexpectedly succeeded." }
    }

    Step "Restart Bootstrap/Consensus and verify recovery" {
        docker compose -f $composeFile restart node-a node-b | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "docker restart failed." }
        $raft = $null
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        do {
            try {
                $raft = Require-HttpJson "http://localhost:7442/api/consensus/raft/status"
                if ($raft.operational) { break }
            }
            catch {
                Start-Sleep -Seconds 2
            }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $deadline)
        if ($null -eq $raft -or -not $raft.operational) {
            throw "Raft cluster did not recover to an operational state after restart."
        }
        $network = Require-HttpJson "http://localhost:7442/api/network/status"
        if ($network.channels.Count -eq 0) { throw "No committed channels after restart." }
    }

    Write-Host ""
    Write-Host "Nexus product smoke passed."
}
catch {
    Write-Host ""
    Write-Host "Nexus product smoke failed: $($_.Exception.Message)"
    $failureComposeFile = if ($RaftTransport -eq "Iroh") { $irohComposeFile } else { $composeFile }
    docker compose -f $failureComposeFile logs --tail=160 | Out-File -FilePath (Join-Path $dataRoot "docker-smoke.log") -Encoding utf8
    if (-not $KeepContainersOnFailure) {
        docker compose -f $failureComposeFile down --remove-orphans | Out-Host
    }
    $env:RAFT_SMOKE_DATA_ROOT = $previousRaftSmokeDataRoot
    $env:IROH_RAFT_SMOKE_DATA_ROOT = $previousIrohRaftSmokeDataRoot
    $env:NODE_A_IROH_ID = $previousNodeAIrohId
    $env:NODE_B_IROH_ID = $previousNodeBIrohId
    $env:NODE_C_IROH_ID = $previousNodeCIrohId
    exit 1
}

if (-not $KeepContainersOnFailure) {
    docker compose -f $composeFile down --remove-orphans | Out-Host
}
$env:RAFT_SMOKE_DATA_ROOT = $previousRaftSmokeDataRoot
$env:IROH_RAFT_SMOKE_DATA_ROOT = $previousIrohRaftSmokeDataRoot
$env:NODE_A_IROH_ID = $previousNodeAIrohId
$env:NODE_B_IROH_ID = $previousNodeBIrohId
$env:NODE_C_IROH_ID = $previousNodeCIrohId
