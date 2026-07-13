import { test, expect } from '@playwright/test';
import {
  attachPageDiagnostics,
  attachRuntimeLogs,
  createRunRoot,
  expectNoBrowserErrors,
  restartNode,
  setupUrl,
  startNode,
  startUi,
  stopAll,
  waitForJson
} from '../fixtures/nexus-runtime';
import { uniqueName } from '../fixtures/test-data';

test('local private node setup works without Raft or Iroh', async ({ page }, testInfo) => {
  const root = await createRunRoot(testInfo);
  let node = await startNode(`${root}/node`);
  const ui = await startUi(`${root}/ui`);
  const browserErrors = attachPageDiagnostics(page);

  try {
    await page.goto(setupUrl(ui, node));
    await page.getByRole('button', { name: 'Local private node' }).click();
    await page.getByLabel('Local node name').fill(uniqueName('Browser Local'));
    await page.getByRole('button', { name: 'Create local node', exact: true }).click();
    await expect(page.getByText('Setup configuration was created. Restart the node to activate it.')).toBeVisible();

    node = await restartNode(node);
    const state = await waitForJson(`${node.url}/api/setup/state`);
    expect(state.state).toBe('Ready');
    expect(state.role).toBe('Local');

    const network = await waitForJson(`${node.url}/api/network/status`);
    expect(network.finalityMode).toBe('Immediate');
    expect(network.irohEnabled).toBe(false);
    expect(network.localRaftRequested).toBe(false);
    await expectNoBrowserErrors(browserErrors);
  } finally {
    await attachRuntimeLogs(testInfo, node.process, ui.process);
    await stopAll(ui.process, node.process);
  }
});
