[CmdletBinding()]
param(
    [string]$DataRoot = ""
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $scriptRoot "..\\.tmp\\local-raft-pair"
}

$dataRootFull = [System.IO.Path]::GetFullPath($DataRoot)

if (-not (Test-Path $dataRootFull)) {
    Write-Host "No local Raft pair data directory found at $dataRootFull."
    return
}

Get-ChildItem -Path $dataRootFull -Filter "node.pid" -Recurse | ForEach-Object {
    $pidValue = Get-Content -Path $_.FullName -ErrorAction SilentlyContinue
    $parsedPid = 0
    if ([int]::TryParse($pidValue, [ref]$parsedPid)) {
        $targetPid = $parsedPid
        try {
            $process = Get-Process -Id $targetPid -ErrorAction Stop
            Stop-Process -Id $process.Id -Force
            Write-Host "Stopped PID $targetPid from $($_.DirectoryName)."
        }
        catch {
            Write-Host "PID $targetPid is not running."
        }
    }
}
