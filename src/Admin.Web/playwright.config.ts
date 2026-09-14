import { defineConfig, devices } from '@playwright/test';

const url = 'http://127.0.0.1:5300';

export default defineConfig({
  testDir: './e2e',
  // Refuses to run against a reused host that is not in FakePlatform mode: the smoke wipes volumes.
  globalSetup: './e2e/global-setup.ts',
  timeout: 30_000,
  fullyParallel: false,
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI'] ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: { baseURL: url, trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    // The host serves the SPA from wwwroot, so `npm run build` must have run first.
    command: 'dotnet run --project ../Admin.Host --no-build -- --Admin:FakePlatform=true',
    cwd: __dirname,
    url: `${url}/api/config`,
    reuseExistingServer: !process.env['CI'],
    timeout: 120_000,
  },
});
