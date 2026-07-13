import { test, expect } from '@playwright/test';
import {
  attachPageDiagnostics,
  attachRuntimeLogs,
  createRunRoot,
  dashboardUrl,
  expectNoBrowserErrors,
  restartNode,
  setupUrl,
  startNode,
  startUi,
  stopAll,
  waitForJson
} from '../fixtures/nexus-runtime';
import { uniqueName } from '../fixtures/test-data';

test('local node uses UI signed intents and browser queue retries after node reconnect', async ({ page }, testInfo) => {
  const root = await createRunRoot(testInfo);
  let node = await startNode(`${root}/node`);
  const ui = await startUi(`${root}/ui`);
  const browserErrors = attachPageDiagnostics(page);

  try {
    await page.goto(setupUrl(ui, node));
    await page.getByRole('button', { name: 'Local private node' }).click();
    await page.getByLabel('Local node name').fill(uniqueName('Local Browser Flow'));
    await page.getByRole('button', { name: 'Create local node', exact: true }).click();
    await expect(page.getByText('Setup configuration was created. Restart the node to activate it.')).toBeVisible();

    node = await restartNode(node);
    await page.goto(dashboardUrl(ui, node));
    await expect(page).not.toHaveURL(/\/setup/);
    await expect(page.getByText('Sign in to Nexus Boards')).toBeVisible();

    const userName = uniqueName('edgeuser').replace(/[^a-zA-Z0-9]/g, '');
    await page.getByLabel('User name').fill(userName);
    await page.getByRole('button', { name: 'Create Wallet' }).click();
    await expect(page.getByText(`Wallet created for '${userName}'.`)).toBeVisible();
    await page.getByLabel('Wallet password').fill('StrongBrowserWalletPassword123!');
    const download = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Download File & Finish' }).click();
    await download;
    await expect(page.getByRole('button', { name: 'Sign Out' })).toBeVisible();

    const projectName = uniqueName('UiProject').replace(/[^a-zA-Z0-9]/g, '');
    await page.getByLabel('New project name').fill(projectName);
    await page.getByRole('button', { name: 'Create', exact: true }).click();
    const committed = await waitForCommittedIntent(node.url, 'CreateProject', 60_000);
    expect(committed.status).toBe('Committed');
    expect(committed.intentId).toBeTruthy();
    expect(committed.proposalId).toContain(committed.intentId);
    expect(committed.committedBlockIndex).toBeGreaterThanOrEqual(0);
    expect(committed.committedBlockHash).toBeTruthy();
    await expect(page.getByText(new RegExp(`Project ${projectName}`, 'i'))).toBeVisible({ timeout: 30_000 });

    const statusBefore = await waitForJson(`${node.url}/api/network/status`);
    const latestBefore = latestChannel(statusBefore);
    expect(latestBefore.latestIndex).toBeGreaterThanOrEqual(0);

    await stopAll(node.process);
    await expect.poll(async () => {
      try {
        await fetch(`${node.url}/healthz`);
        return 'online';
      } catch {
        return 'offline';
      }
    }, { timeout: 20_000 }).toBe('offline');
    const offlineProjectName = uniqueName('OfflineProject').replace(/[^a-zA-Z0-9]/g, '');
    await page.getByLabel('New project name').fill(offlineProjectName);
    await page.getByRole('button', { name: 'Create', exact: true }).click();
    await expect(page.getByText('Action queued locally')).toBeVisible({ timeout: 30_000 });

    const queued = await page.evaluate(() => window.localStorage.getItem('nexus_browser_operation_queue_v1') ?? '');
    expect(queued).toContain('QueuedLocally');
    expect(queued).toContain(offlineProjectName);
    expect(queued).not.toContain('privateKey');
    expect(queued).not.toContain('sidecar');
    expect(queued).not.toContain('NodeDbPassword');
    expect(await page.evaluate(() => window.localStorage.getItem('nexus_priv'))).toBeNull();

    node = await restartNode(node);
    browserErrors.splice(0, browserErrors.length);
    await page.goto(dashboardUrl(ui, node));
    await page.getByPlaceholder('Wallet password').fill('StrongBrowserWalletPassword123!');
    await page.getByRole('button', { name: 'Unlock', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Sign Out' })).toBeVisible();
    await page.getByRole('button', { name: /Network/ }).click();
    await page.getByRole('button', { name: 'Refresh Peers' }).click();
    const offlineCommitted = await waitForCommittedIntent(node.url, 'CreateProject', 90_000, committed.committedBlockIndex);
    expect(offlineCommitted.status).toBe('Committed');
    await expect.poll(async () => page.evaluate(() => window.localStorage.getItem('nexus_browser_operation_queue_v1') ?? '[]'), {
      timeout: 30_000
    }).not.toContain(offlineProjectName);

    const statusAfter = await waitForJson(`${node.url}/api/network/status`);
    const latestAfter = latestChannel(statusAfter);
    expect(latestAfter.latestIndex).toBeGreaterThanOrEqual(latestBefore.latestIndex);
    await expectNoBrowserErrors(browserErrors);
  } finally {
    await attachRuntimeLogs(testInfo, node.process, ui.process);
    await stopAll(ui.process, node.process);
  }
});

async function waitForCommittedIntent(nodeUrl: string, operationType: string, timeoutMs: number, minCommittedBlockIndex = -1): Promise<any> {
  const deadline = Date.now() + timeoutMs;
  let lastResponse: any = null;
  while (Date.now() < deadline) {
    lastResponse = await waitForJson(`${nodeUrl}/api/network/intents?limit=50`, 10_000);
    const match = lastResponse.intents?.find((intent: any) =>
      intent.operationType === operationType &&
      String(intent.status).toLowerCase() === 'committed' &&
      String(intent.intentId).length > 0 &&
      Number(intent.committedBlockIndex ?? -1) > minCommittedBlockIndex);
    if (match) {
      return match;
    }
    await new Promise(resolve => setTimeout(resolve, 500));
  }

  throw new Error(`Timed out waiting for committed ${operationType} intent after block ${minCommittedBlockIndex}. Last response: ${JSON.stringify(lastResponse)}`);
}

function latestChannel(status: any): any {
  const channels = status.channels ?? [];
  if (channels.length === 0) {
    throw new Error(`Network status has no channels: ${JSON.stringify(status)}`);
  }

  return channels.sort((left: any, right: any) => Number(right.latestIndex) - Number(left.latestIndex))[0];
}
