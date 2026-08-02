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
import { fakeIrohNodeId, uniqueName } from '../fixtures/test-data';

test('create network through UI persists Iroh setup after restart', async ({ page }, testInfo) => {
  const root = await createRunRoot(testInfo);
  let node = await startNode(`${root}/node`);
  const ui = await startUi(`${root}/ui`);
  const browserErrors = attachPageDiagnostics(page);
  const networkName = uniqueName('Browser Network');

  try {
    await page.goto(setupUrl(ui, node));
    await page.getByLabel('Network name').fill(networkName);
    await page.getByLabel('Mode').selectOption('all-in-one');
    await page.getByLabel('Raft transport').selectOption('Iroh');
    await page.getByLabel('Public HTTP URL').fill(node.url);
    await page.getByLabel('Local Iroh node id').fill(fakeIrohNodeId(networkName));
    await page.getByRole('button', { name: 'Create network', exact: true }).click();

    await expect(page.getByText('Setup configuration was created. Restart the node to activate it.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Copy invite' })).toBeVisible();
    const inviteText = await page.locator('textarea[readonly]').inputValue();
    const invite = JSON.parse(inviteText);
    expect(invite.networkName).toBe(networkName);
    expect(invite.supportedRaftTransports).toContain('Iroh');

    node = await restartNode(node);
    const state = await waitForJson(`${node.url}/api/setup/state`);
    expect(state.state).toBe('Ready');
    expect(state.fullNodeConfigured).toBe(true);

    const network = await waitForJson(`${node.url}/api/network/status`);
    expect(network.raftTransport).toBe('Iroh');
    expect(network.irohEnabled).toBe(true);
    expect(network.nodeIdentityFingerprint).toMatch(/^[0-9a-f]{16}$/);

    await page.goto(dashboardUrl(ui, node));
    await expect(page).not.toHaveURL(/\/setup/);
    await expect(page.getByText('Sign in to Nexus Boards')).toBeVisible();
    await expectNoBrowserErrors(browserErrors);
  } finally {
    await attachRuntimeLogs(testInfo, node.process, ui.process);
    await stopAll(ui.process, node.process);
  }
});
