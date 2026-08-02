# Performance & Security Checklist

Use this checklist to support thesis Section 4 evidence.

## Automated checks

- `dotnet test Blockchain.Tests/Blockchain.Tests.csproj`
- `dotnet test Blockchain.UI.Tests/Blockchain.UI.Tests.csproj`

Must include:

- signature validation,
- RBAC rejection paths,
- replay guard tests,
- channel consistency tests,
- component rendering tests for audit trail and key-binding inputs.

## Manual security checks

1. Send malformed signatures for write/read calls.
2. Try project reads with wrong key binding.
3. Try joining SignalR project group without membership.
4. Confirm the browser UI does not contain or submit `NodeAdminToken`; peer registration must stay server-side/admin-only.
5. Send duplicate webhook request.

Expected:

- rejected requests,
- explicit security status messages/logs.

## Manual performance checks

1. Create high-frequency task/document updates.
2. Measure block throughput in Analytics (`Blocks / Minute`).
3. Compare with second peer synchronization enabled.
4. Confirm security audit remains valid during load.

## Evidence to capture

- screenshots of analytics and audit trail,
- terminal test output,
- node logs showing rejection of unauthorized operations.
