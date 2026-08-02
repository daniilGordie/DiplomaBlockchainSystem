[CmdletBinding()]
param(
    [string]$DataRoot = "",
    [switch]$SkipBuild,
    [switch]$UsePersistentMembership,
    [int]$WarmupSeconds = 12
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $scriptRoot "..\\.tmp\\local-raft-grpc-smoke"
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
$dataRootFull = [System.IO.Path]::GetFullPath($DataRoot)
$tmpRootFull = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ".tmp"))
$smokeProject = Join-Path $repoRoot "tools\\RaftGrpcSmoke\\RaftGrpcSmoke.csproj"
$producerKeyPath = Join-Path $dataRootFull "shared\\producer-key.dat"
$producerPassword = "local-raft-producer-password"
$dbPassword = "local-raft-db-password"

if (-not $dataRootFull.StartsWith($tmpRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to run destructive local smoke outside repository .tmp directory: $dataRootFull"
}

if (Test-Path $dataRootFull) {
    Remove-Item -LiteralPath $dataRootFull -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $dataRootFull | Out-Null

if (-not $SkipBuild) {
    dotnet build $smokeProject | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Preparing PoC/Raft smoke seed..."
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
    Write-Host "Starting local PoC/Raft pair..."
    $startArgs = @{
        SkipBuild = $true
        DataRoot = $dataRootFull
        ProducerKeyPath = $producerKeyPath
        ProducerPrivateKeyPassword = $producerPassword
        KeepAliveSeconds = $WarmupSeconds
    }
    if ($UsePersistentMembership) {
        $startArgs["UsePersistentMembership"] = $true
    }

    & (Join-Path $scriptRoot "Start-LocalRaftPair.ps1") @startArgs

    Write-Host ""
    Write-Host "Submitting producer-signed gRPC proposal and verifying follower storage..."
    dotnet run --no-build --project $smokeProject -- submit `
        --data-root $dataRootFull `
        --producer-key-path $producerKeyPath `
        --producer-password $producerPassword `
        --db-password $dbPassword `
        --submit-url "http://localhost:7042" `
        --verify-urls "http://localhost:7042,http://localhost:7043" `
        --sync-token "local-raft-sync-token" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "RaftGrpcSmoke submit failed with exit code $LASTEXITCODE."
    }

    Write-Host ""
    Write-Host "PoC/Raft live gRPC smoke passed."
}
finally {
    & (Join-Path $scriptRoot "Stop-LocalRaftPair.ps1") -DataRoot $dataRootFull
}
