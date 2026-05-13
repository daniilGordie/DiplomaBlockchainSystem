param(
    [string]$NodeWebhookUrl = "https://localhost:7041/api/webhooks/git",
    [string]$ProjectId,
    [string]$Repository = (Split-Path -Leaf (Get-Location)),
    [string]$WebhookSecret
)

$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$gitDir = Join-Path $repoRoot ".git"
$hookDir = Join-Path $gitDir "hooks"
$hookPath = Join-Path $hookDir "post-commit"
$hookProject = Join-Path $repoRoot "BlockChain.GitHook\BlockChain.GitHook.csproj"
$hookProjectForShell = $hookProject -replace '\\', '/'

if (!(Test-Path $gitDir)) {
    throw "This script must be run from the repository root."
}

if (!(Test-Path $hookProject)) {
    throw "Git hook project was not found: $hookProject"
}

if ([string]::IsNullOrWhiteSpace($ProjectId)) {
    throw "ProjectId is required. Pass -ProjectId <your-project-id>."
}

if ([string]::IsNullOrWhiteSpace($WebhookSecret)) {
    throw "WebhookSecret is required. Pass -WebhookSecret <secret matching Blockchain.Node WebhookSecret>."
}

New-Item -ItemType Directory -Force -Path $hookDir | Out-Null

$hookContent = @"
#!/bin/sh
export NEXUS_NODE_WEBHOOK_URL="$NodeWebhookUrl"
export NEXUS_PROJECT_ID="$ProjectId"
export NEXUS_REPOSITORY="$Repository"
export NEXUS_WEBHOOK_SECRET="$WebhookSecret"
dotnet run --project "$hookProjectForShell" --no-restore
"@

Set-Content -LiteralPath $hookPath -Value $hookContent -Encoding ASCII

Write-Host "Installed post-commit hook:"
Write-Host "  $hookPath"
Write-Host "Webhook URL:"
Write-Host "  $NodeWebhookUrl"
Write-Host "Project ID:"
Write-Host "  $ProjectId"
