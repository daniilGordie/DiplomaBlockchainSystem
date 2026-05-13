# Defense Smoke Runbook

This is a concise operator checklist for live defense.

## Prerequisites

1. Configure secrets from `SECURITY_SETUP.md`:
   - `NodeDbPassword`
   - `NodeAdminToken`
   - `WebhookSecret`
   - `OraclePrivateKeyPassword`
   - `OraclePublicKey`
   - `P2P:SyncToken`
2. Ensure UI `NodeAdminToken` matches node token.
3. Start node and UI.

## Scenario A: Auth + project access

1. Create wallet for user A.
2. Create project.
3. Confirm project appears in user A list.
4. Open analytics and verify chain/security widgets load.

Expected:

- project-scoped reads work for bound user key.

## Scenario B: Team key binding + RBAC

1. User B shares public key + fingerprint.
2. User A invites user B in Team tab with key and expected fingerprint.
3. User B logs in with bound key and accesses project.

Expected:

- role assignment accepted,
- user B can read/write project data,
- spoofed key attempts are rejected.

## Scenario C: Task + document + governance

1. Create task, change status.
2. Create/update document and open version history.
3. Create proposal and cast votes.

Expected:

- state indexes update,
- history is available,
- governance transitions by quorum.

## Scenario D: Audit trail evidence

1. Open Analytics tab.
2. Check `Blockchain Audit Trail` table.

Expected:

- rows show event/user/entity/block/hash/time.

## Scenario E: Webhook anchoring

1. Trigger git webhook payload with valid HMAC.
2. Observe commit event in repository/history.

Expected:

- node anchors commit server-side,
- duplicate webhook is ignored by replay guard.

## Scenario F: P2P and secure adoption

1. Start second node with matching `P2P:SyncToken`.
2. Add peer using `NodeAdminToken`.
3. Produce blocks on node A and sync node B.

Expected:

- chains synchronize,
- invalid candidate chains are rejected by replay validation.
