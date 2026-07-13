import { test, expect } from '@playwright/test';
import {
  attachPageDiagnostics,
  attachRuntimeLogs,
  createRunRoot,
  expectNoBrowserErrors,
  setupUrl,
  startNode,
  startUi,
  stopAll
} from '../fixtures/nexus-runtime';
import { fakeIrohNodeId, uniqueName } from '../fixtures/test-data';

test('edge join setup accepts signed invite through UI without Raft endpoint', async ({ page, context }, testInfo) => {
  const root = await createRunRoot(testInfo);
  const bootstrap = await startNode(`${root}/bootstrap`);
  const edge = await startNode(`${root}/edge`);
  const ui = await startUi(`${root}/ui`);
  const browserErrors = attachPageDiagnostics(page);
  const networkName = uniqueName('Join Browser Network');

  try {
    await page.goto(setupUrl(ui, bootstrap));
    await page.getByLabel('Network name').fill(networkName);
    await page.getByLabel('Mode').selectOption('all-in-one');
    await page.getByLabel('Raft transport').selectOption('Iroh');
    await page.getByLabel('Public HTTP URL').fill(bootstrap.url);
    await page.getByLabel('Local Iroh node id').fill(fakeIrohNodeId(networkName));
    await page.getByRole('button', { name: 'Create network', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Copy invite' })).toBeVisible();
    const inviteText = await page.locator('textarea[readonly]').inputValue();

    const edgePage = await context.newPage();
    const edgeErrors = attachPageDiagnostics(edgePage);
    await edgePage.goto(setupUrl(ui, edge));
    await edgePage.getByRole('button', { name: 'Join network' }).click();
    await edgePage.getByLabel('Connection invite').fill(inviteText);
    await edgePage.getByRole('button', { name: 'Validate invite' }).click();
    await expect(edgePage.getByText(`Network: ${networkName}`)).toBeVisible();
    await edgePage.getByRole('button', { name: 'Join as Edge' }).click();
    await expect(edgePage.getByText('Setup configuration was created. Restart the node to activate it.')).toBeVisible();

    const edgeStateResponse = await fetch(`${edge.url}/api/setup/state`);
    expect(edgeStateResponse.ok).toBe(true);
    expect(JSON.parse(inviteText).SupportedRaftTransports).toContain('Iroh');
    await expectNoBrowserErrors([...browserErrors, ...edgeErrors]);
  } finally {
    await attachRuntimeLogs(testInfo, bootstrap.process, edge.process, ui.process);
    await stopAll(ui.process, edge.process, bootstrap.process);
  }
});
