[CmdletBinding()]
param(
    [string]$DataRoot = "",
    [switch]$SkipBuild,
    [switch]$KeepRunning,
    [int]$WarmupSeconds = 16
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $scriptRoot "..\\.tmp\\docker-raft-smoke"
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
$dataRootFull = [System.IO.Path]::GetFullPath($DataRoot)
$tmpRootFull = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ".tmp"))
$smokeProject = Join-Path $repoRoot "tools\\RaftGrpcSmoke\\RaftGrpcSmoke.csproj"
$composeFile = Join-Path $scriptRoot "docker-compose.raft-smoke.yml"
$producerKeyPath = Join-Path $dataRootFull "shared\\producer-key.dat"
$producerPassword = "local-raft-producer-password"
$dbPassword = "local-raft-db-password"
$previousRaftSmokeDataRoot = $env:RAFT_SMOKE_DATA_ROOT
$env:RAFT_SMOKE_DATA_ROOT = $dataRootFull

if (-not $dataRootFull.StartsWith($tmpRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean Docker Raft smoke data outside repository .tmp directory: $dataRootFull"
}

if (-not $SkipBuild) {
    dotnet build $smokeProject | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

docker compose -f $composeFile down --remove-orphans | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "docker compose down failed with exit code $LASTEXITCODE."
}

if (Test-Path $dataRootFull) {
    Remove-Item -LiteralPath $dataRootFull -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $dataRootFull | Out-Null

Write-Host "Preparing Docker PoC/Raft smoke seed..."
dotnet run --no-build --project $smokeProject -- prepare `
    --data-root $dataRootFull `
    --producer-key-path $producerKeyPath `
    --producer-password $producerPassword `
    --db-password $dbPassword | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "RaftGrpcSmoke prepare failed with exit code $LASTEXITCODE."
}

try {
    Write-Host ""
    Write-Host "Starting Docker PoC/Raft stack..."
    if ($SkipBuild) {
        docker compose -f $composeFile up -d --no-build | Out-Host
    }
    else {
        docker compose -f $composeFile up -d --build | Out-Host
    }
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose up failed with exit code $LASTEXITCODE."
    }

    $deadline = (Get-Date).AddSeconds(90)
    do {
        try {
            $nodeA = Invoke-RestMethod -Uri "http://localhost:7442/api/consensus/raft/status" -TimeoutSec 5
            $nodeB = Invoke-RestMethod -Uri "http://localhost:7443/api/consensus/raft/status" -TimeoutSec 5
            if ($nodeA.operational -and $nodeB.operational -and
                -not [string]::IsNullOrWhiteSpace([string]$nodeA.leader) -and
                [string]$nodeA.leader -eq [string]$nodeB.leader) {
                break
            }
        }
        catch {
            Start-Sleep -Seconds 2
        }

        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    if ($null -eq $nodeA -or $null -eq $nodeB -or
        -not $nodeA.operational -or -not $nodeB.operational -or
        [string]::IsNullOrWhiteSpace([string]$nodeA.leader) -or
        [string]$nodeA.leader -ne [string]$nodeB.leader) {
        throw "Raft cluster did not become operational before timeout."
    }

    Start-Sleep -Seconds $WarmupSeconds

    Write-Host ""
    Write-Host "Submitting producer-signed Docker gRPC proposal and verifying follower storage..."
    dotnet run --no-build --project $smokeProject -- submit `
        --data-root $dataRootFull `
        --producer-key-path $producerKeyPath `
        --producer-password $producerPassword `
        --db-password $dbPassword `
        --submit-url "http://localhost:7442" `
        --verify-urls "http://localhost:7442,http://localhost:7443" `
        --sync-token "local-raft-sync-token" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "RaftGrpcSmoke submit failed with exit code $LASTEXITCODE."
    }

    Write-Host ""
    Write-Host "Submitting signed intent CreateProject through HTTP intent endpoint..."
    dotnet run --no-build --project $smokeProject -- create-project `
        --submit-url "http://localhost:7442" `
        --verify-urls "http://localhost:7442,http://localhost:7443" `
        --project-id "IntentSmoke_$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))" `
        --user "IntentSmokeUser" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "RaftGrpcSmoke create-project signed intent failed with exit code $LASTEXITCODE."
    }

    Write-Host ""
    Write-Host "Docker PoC/Raft smoke passed."
}
finally {
    $env:RAFT_SMOKE_DATA_ROOT = $previousRaftSmokeDataRoot
    if (-not $KeepRunning) {
        docker compose -f $composeFile down --remove-orphans | Out-Host
    }
}
