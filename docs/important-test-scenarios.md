# Important Test Scenarios (Section 4 Support)

This file documents additional verification scenarios for the "Important" block:

- frontend/component checks,
- API/security negative checks,
- performance/scalability checks.

## 1) Frontend component tests

Run:

```powershell
dotnet test Blockchain.UI.Tests\Blockchain.UI.Tests.csproj
```

Covers:

- analytics audit trail rendering,
- empty audit trail state,
- team key-binding inputs (public key + fingerprint).

## 2) Backend security tests

Run:

```powershell
dotnet test Blockchain.Tests\Blockchain.Tests.csproj
```

Covers (non-exhaustive):

- signature format and validation,
- channel/payload consistency guards,
- RBAC and identity spoofing rejection,
- role assignment key-binding constraints,
- replay-guard duplicate webhook suppression,
- secure chain-adoption rejection on invalid contract replay.

## 3) Manual rate-limit abuse check

1. Start `Blockchain.Node`.
2. Send many requests from one source IP to an HTTP endpoint quickly.
3. Confirm HTTP `429` responses appear after threshold.

Expected:

- short bursts are served,
- sustained flooding receives `429 Too many requests`.

## 4) Manual scalability check (prototype)

1. Start two nodes with `P2P:SyncToken` configured.
2. Create high-frequency task/document updates in one project.
3. Add the second node as peer with `NodeAdminToken`.
4. Validate:
   - project chain sync completes,
   - analytics block throughput is non-zero,
   - security audit remains chain-valid.
