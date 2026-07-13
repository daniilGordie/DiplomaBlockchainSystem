import { test, expect } from '@playwright/test';
import {
  attachPageDiagnostics,
  attachRuntimeLogs,
  createRunRoot,
  dashboardUrl,
  expectNoBrowserErrors,
  postJson,
  restartNode,
  startNode,
  startUi,
  stopAll,
  waitForJson
} from '../fixtures/nexus-runtime';

test('configured node opens working UI instead of setup wizard', async ({ page }, testInfo) => {
  const root = await createRunRoot(testInfo);
  let node = await startNode(`${root}/node`);
  const ui = await startUi(`${root}/ui`);
  const browserErrors = attachPageDiagnostics(page);

  try {
    await postJson(`${node.url}/api/setup/local`, {
      networkName: 'Runtime UI Node',
      networkId: null,
      nodeId: null
    });
    node = await restartNode(node);
    const state = await waitForJson(`${node.url}/api/setup/state`);
    expect(state.state).toBe('Ready');

    await page.goto(dashboardUrl(ui, node));
    await expect(page).not.toHaveURL(/\/setup/);
    await expect(page.getByText('Sign in to Nexus Boards')).toBeVisible();
    await expectNoBrowserErrors(browserErrors);
  } finally {
    await attachRuntimeLogs(testInfo, node.process, ui.process);
    await stopAll(ui.process, node.process);
  }
});
