import { test, expect } from '@playwright/test';
import path from 'node:path';
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
import { backupPassword, uniqueName } from '../fixtures/test-data';

test('restore node activates encrypted backup through UI upload', async ({ page, context }, testInfo) => {
  const root = await createRunRoot(testInfo);
  let source = await startNode(`${root}/source`);
  let target = await startNode(`${root}/target`);
  const ui = await startUi(`${root}/ui`);
  const browserErrors = attachPageDiagnostics(page);
  const backupPath = path.join(root, 'node.nexus-backup');

  try {
    await page.goto(setupUrl(ui, source));
    await page.getByRole('button', { name: 'Local private node' }).click();
    await page.getByLabel('Local node name').fill(uniqueName('Restore Browser Source'));
    await page.getByRole('button', { name: 'Create local node', exact: true }).click();
    await expect(page.getByText('Setup configuration was created. Restart the node to activate it.')).toBeVisible();

    source = await restartNode(source);
    const sourceState = await waitForJson(`${source.url}/api/setup/state`);
    const sourceNetwork = await waitForJson(`${source.url}/api/network/status`);

    await page.goto(setupUrl(ui, source));
    await page.getByRole('button', { name: 'Restore node' }).click();
    await page.locator('input[name="password"]').fill(backupPassword);
    const downloadPromise = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Download encrypted backup' }).click();
    const download = await downloadPromise;
    await download.saveAs(backupPath);

    const restorePage = await context.newPage();
    const restoreErrors = attachPageDiagnostics(restorePage);
    await restorePage.goto(setupUrl(ui, target));
    await restorePage.getByLabel('Node API endpoint').fill(target.url);
    await restorePage.getByRole('button', { name: 'Refresh state' }).click();
    await expect(restorePage.getByText('State NotConfigured')).toBeVisible();
    await restorePage.getByRole('button', { name: 'Restore node' }).click();
    await restorePage.locator('#setupRestoreFile').setInputFiles(backupPath);
    await expect(restorePage.getByText(/Selected .*node\.nexus-backup/)).toBeVisible();
    await restorePage.locator('#setupRestorePassword').fill('wrong-password');
    await restorePage.getByRole('button', { name: 'Restore backup' }).click();
    await expect(restorePage.getByText(/incorrect|damaged/i)).toBeVisible();
    restoreErrors.length = 0;

    await restorePage.locator('#setupRestorePassword').fill(backupPassword);
    await restorePage.getByRole('button', { name: 'Restore backup' }).click();
    await expect(restorePage.getByText('Backup restored. Restart the node to activate restored services.')).toBeVisible();

    target = await restartNode(target);
    const targetState = await waitForJson(`${target.url}/api/setup/state`);
    const targetNetwork = await waitForJson(`${target.url}/api/network/status`);
    expect(targetState.state).toBe('Ready');
    expect(targetState.nodeId).toBe(sourceState.nodeId);
    expect(targetNetwork.nodeIdentityFingerprint).toBe(sourceNetwork.nodeIdentityFingerprint);
    await expectNoBrowserErrors([...browserErrors, ...restoreErrors]);
  } finally {
    await attachRuntimeLogs(testInfo, source.process, target.process, ui.process);
    await stopAll(ui.process, target.process, source.process);
  }
});
