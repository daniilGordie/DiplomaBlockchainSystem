import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 120_000,
  expect: {
    timeout: 15_000
  },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [
    ['list'],
    ['html', { outputFolder: '../playwright-report', open: 'never' }]
  ],
  outputDir: '../test-results',
  use: {
    ...devices['Desktop Chrome'],
    browserName: 'chromium',
    headless: process.env.PLAYWRIGHT_HEADED === '1' ? false : true,
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 20_000,
    navigationTimeout: 45_000
  },
  projects: [
    {
      name: 'chromium',
      use: { browserName: 'chromium' }
    }
  ]
});
