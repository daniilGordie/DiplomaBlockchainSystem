import { test, expect } from '@playwright/test';
import {
  attachPageDiagnostics,
  attachRuntimeLogs,
  createRunRoot,
  dashboardUrl,
  expectNoBrowserErrors,
  setupUrl,
  startNode,
  startUi,
  stopAll,
  waitForJson
} from '../fixtures/nexus-runtime';

test('first run opens setup wizard and exposes diagnostics', async ({ page }, testInfo) => {
  const root = await createRunRoot(testInfo);
  const node = await startNode(`${root}/node`);
  const ui = await startUi(`${root}/ui`);
  const browserErrors = attachPageDiagnostics(page);

  try {
    await page.goto(dashboardUrl(ui, node));
    await expect(page).toHaveURL(/\/setup/);
    await expect(page.getByRole('heading', { name: 'Nexus setup' })).toBeVisible();
    await expect(page.getByLabel('Node API endpoint')).toHaveValue(node.url);
    await expect(page.getByRole('link', { name: 'Diagnostics' })).toHaveAttribute('href', `${node.url}/api/setup/diagnostics`);
    const state = await waitForJson(`${node.url}/api/setup/state`);
    expect(state.state).toBe('NotConfigured');
    await expectNoBrowserErrors(browserErrors);
  } finally {
    await attachRuntimeLogs(testInfo, node.process, ui.process);
    await stopAll(ui.process, node.process);
  }
});

test('setup wizard can be opened directly with an isolated node endpoint', async ({ page }, testInfo) => {
  const root = await createRunRoot(testInfo);
  const node = await startNode(`${root}/node`);
  const ui = await startUi(`${root}/ui`);
  const browserErrors = attachPageDiagnostics(page);

  try {
    await page.goto(setupUrl(ui, node));
    await expect(page.getByRole('heading', { name: 'Nexus setup' })).toBeVisible();
    await expect(page.getByText('State NotConfigured')).toBeVisible();
    await page.getByRole('button', { name: 'Refresh state' }).click();
    await expect(page.getByText('State NotConfigured')).toBeVisible();
    await expectNoBrowserErrors(browserErrors);
  } finally {
    await attachRuntimeLogs(testInfo, node.process, ui.process);
    await stopAll(ui.process, node.process);
  }
});
