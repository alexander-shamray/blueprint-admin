import { defineConfig, devices } from '@playwright/test';
import { OFF_PORT, OFF_URL } from './e2e/hosts';

const url = 'http://127.0.0.1:5300';

export default defineConfig({
  testDir: './e2e',
  // Refuses to run against a reused host that is not in FakePlatform mode: the smoke wipes volumes.
  globalSetup: './e2e/global-setup.ts',
  timeout: 30_000,
  fullyParallel: false,
  // One worker: every spec file drives one FakePlatform host, and its ComposeGate refuses a second Compose-changing job
  // (Stack's Up, Down and Reset; Broker's Rebuild) with 409, so two files on two workers could fail each other.
  workers: 1,
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI'] ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: { baseURL: url, trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      // The host serves the SPA from wwwroot, so `npm run build` must have run first.
      command: 'dotnet run --project ../Admin.Host --no-build -- --Admin:FakePlatform=true',
      cwd: __dirname,
      url: `${url}/api/config`,
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
    {
      // A second fake host with the assistant switched off (AI assistant design §9.1), for assistant-off.spec.ts.
      command: `dotnet run --project ../Admin.Host --no-build -- --Admin:FakePlatform=true --Admin:Port=${OFF_PORT} --Admin:Assistant:Provider=off`,
      cwd: __dirname,
      url: `${OFF_URL}/api/config`,
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
  ],
});
