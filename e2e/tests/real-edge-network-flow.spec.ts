import { test, expect } from '@playwright/test';
import { spawn } from 'node:child_process';
import path from 'node:path';
import fs from 'node:fs/promises';

const repoRoot = path.resolve(path.join(import.meta.dirname, '..', '..'));
const composeFile = path.join(repoRoot, 'deploy', 'docker-compose.iroh-raft-smoke.yml');

test.describe('real Edge network runtime flow', () => {
  test.setTimeout(900_000);

  test('runs 3-node Raft-over-Iroh core and verifies Edge membership over browser diagnostics', async ({ page }, testInfo) => {
    const smokeLog = path.join(testInfo.outputDir, 'real-edge-product-smoke.log');

    try {
      await runCommand(
        'pwsh',
        [
          '-NoProfile',
          '-ExecutionPolicy',
          'Bypass',
          '-File',
          path.join(repoRoot, 'deploy', 'Invoke-NexusProductSmoke.ps1'),
          '-RaftTransport',
          'Iroh',
          '-SkipBuild',
          '-KeepContainersOnFailure',
          '-TimeoutSeconds',
          '600'
        ],
        smokeLog);

      await page.goto('http://localhost:7452/api/consensus/raft/status');
      const leaderStatus = JSON.parse(await page.locator('body').innerText());
      expect(leaderStatus.configuration.transport).toBe('Iroh');
      expect(leaderStatus.configuration.publicEndPoint).toBe('');
      expect(leaderStatus.transportProof.actualTransport).toBe('Iroh');
      expect(leaderStatus.transportProof.alpn).toBe('nexus/raft/1');
      expect(leaderStatus.members).toHaveLength(3);
      expect(leaderStatus.leader).toContain('iroh://');

      for (const port of [7452, 7453, 7454]) {
        await page.goto(`http://localhost:${port}/api/consensus/raft/status`);
        const status = JSON.parse(await page.locator('body').innerText());
        expect(status.configuration.transport).toBe('Iroh');
        expect(status.configuration.publicEndPoint).toBe('');
        expect(status.configuration.peers.every((peer: { endPoint: string }) => peer.endPoint.startsWith('iroh://'))).toBe(true);
        expect(status.members).toHaveLength(3);
      }

      await submitEdgeJoinRequest(page);
      await changeEdgeMembershipStatus(page, 'Approved', 'browser approval');
      await changeEdgeMembershipStatus(page, 'Revoked', 'browser revoke');
      await changeEdgeMembershipStatus(page, 'Approved', 'browser restore');

      await page.goto('http://localhost:7452/api/network/membership/edge-smoke-1');
      const edgeMembership = JSON.parse(await page.locator('body').innerText());
      expect(edgeMembership.nodeId).toBe('edge-smoke-1');
      expect(edgeMembership.requestedRole).toBe('Edge');
      const capabilities = String(edgeMembership.capabilities).split(',').map(item => item.trim()).filter(Boolean);
      expect(capabilities).toContain('Edge');
      expect(capabilities).toContain('Storage');
      expect(capabilities).not.toContain('Consensus');
      expect(edgeMembership.membershipStatus).toBe('Approved');

      await page.goto('http://localhost:7452/api/network/membership/edge-smoke-1/audit');
      const audit = JSON.parse(await page.locator('body').innerText());
      expect(audit.events.length).toBeGreaterThanOrEqual(2);
      expect(audit.events.some((entry: { newValue: string }) => entry.newValue.includes('Approved'))).toBe(true);
      expect(audit.events.some((entry: { newValue: string }) => entry.newValue.includes('Revoked'))).toBe(true);

      await page.goto('http://localhost:7452/api/network/status');
      const network = JSON.parse(await page.locator('body').innerText());
      expect(network.raftTransport).toBe('Iroh');
      expect(network.channels.length).toBeGreaterThan(0);
      expect(network.knownPeers.some((peer: { nodeId: string; requestedRole: string }) =>
        peer.nodeId === 'edge-smoke-1' && peer.requestedRole === 'Edge')).toBe(true);
    } finally {
      await testInfo.attach('real-edge-product-smoke.log', {
        path: smokeLog,
        contentType: 'text/plain'
      }).catch(() => undefined);
      await runCommand('docker', ['compose', '-f', composeFile, 'down', '--remove-orphans'], path.join(testInfo.outputDir, 'real-edge-cleanup.log'))
        .catch(() => undefined);
    }
  });
});

async function submitEdgeJoinRequest(page: import('@playwright/test').Page): Promise<void> {
  const response = await page.evaluate(async () => {
    const body = {
      networkId: 'nexus-main',
      nodeId: 'edge-smoke-1',
      nodePublicKey: btoa('edge-smoke-public-key'),
      irohUrl: 'iroh://edge-smoke-1',
      irohNodeId: 'edge-smoke-iroh',
      publicEndpoint: '',
      requestedRole: 'Edge',
      appVersion: 'browser-e2e',
      protocolVersion: '1',
      capabilities: ['Edge', 'Storage']
    };

    const result = await fetch('http://localhost:7452/api/network/membership/join', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body)
    });

    return { ok: result.ok, status: result.status, json: await result.json() };
  });

  expect(response.ok, JSON.stringify(response)).toBe(true);
  expect(response.json.success, JSON.stringify(response)).toBe(true);
}

async function changeEdgeMembershipStatus(
  page: import('@playwright/test').Page,
  status: 'Approved' | 'Revoked',
  reason: string): Promise<void> {
  const response = await page.evaluate(async ({ status, reason }) => {
    const result = await fetch('http://localhost:7452/api/network/membership/edge-smoke-1/status', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        status,
        adminToken: 'local-raft-admin-token',
        actor: 'browser-e2e',
        reason
      })
    });

    return { ok: result.ok, statusCode: result.status, json: await result.json() };
  }, { status, reason });

  expect(response.ok, JSON.stringify(response)).toBe(true);
  expect(response.json.success, JSON.stringify(response)).toBe(true);
}

async function runCommand(command: string, args: string[], logPath: string): Promise<void> {
  await fs.mkdir(path.dirname(logPath), { recursive: true });
  return await new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: repoRoot,
      env: sanitizedEnv(),
      windowsHide: true
    });
    const chunks: Buffer[] = [];
    child.stdout.on('data', chunk => chunks.push(Buffer.from(chunk)));
    child.stderr.on('data', chunk => chunks.push(Buffer.from(chunk)));
    child.on('error', reject);
    child.on('exit', async code => {
      await fs.writeFile(logPath, Buffer.concat(chunks));
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`${command} ${args.join(' ')} exited with ${code}`));
      }
    });
  });
}

function sanitizedEnv(): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {};
  for (const [key, value] of Object.entries(process.env)) {
    if (typeof value === 'string') {
      env[key] = value;
    }
  }

  return env;
}
