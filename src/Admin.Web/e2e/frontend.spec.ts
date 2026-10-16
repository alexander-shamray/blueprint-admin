import { expect, test } from '@playwright/test';

// The fake host lives for the whole run and a CI retry re-enters with whatever
// the failed attempt left running, so every test starts from a stopped frontend.
test.beforeEach(async ({ request }) => {
  const stopped = await request.post('/api/stack/frontend/stop');
  expect([200, 409]).toContain(stopped.status());
});

test('the stack screen starts and stops the reference client', async ({ page }) => {
  await page.goto('/stack');
  const status = page.locator('.frontend-status');
  const start = page.getByRole('button', { name: 'Start frontend' });
  const stop = page.getByRole('button', { name: 'Stop frontend' });
  const output = page.locator('app-output-pane pre');

  await expect(status).toContainText('npm start');
  await expect(start).toBeEnabled();
  await expect(stop).toBeDisabled();

  await start.click();
  await expect(output).toContainText('http://localhost:5173/');
  // The buttons follow the 3-second stack poll.
  await expect(status).toContainText('running', { timeout: 10_000 });
  await expect(stop).toBeEnabled();

  await stop.click();
  await expect(output).toContainText('exited -1');
  await expect(status).toContainText('exited -1', { timeout: 10_000 });
  await expect(start).toBeEnabled();
});

test('the device lane shows its three reads and builds for the emulator', async ({ page }) => {
  await page.goto('/stack');
  const reads = page.locator('table.device-reads tbody tr');
  const build = page.getByRole('button', { name: 'Build for emulator' });
  const output = page.locator('app-output-pane pre');

  await expect(reads).toHaveCount(3);
  await expect(reads.nth(0)).toContainText('Emulator attached');
  await expect(reads.nth(1)).toContainText("Gateway admits the app's origin");
  await expect(reads.nth(2)).toContainText('Realm redirect for mobile-app');
  await expect(page.locator('table.device-reads td.state')).toHaveText(['ok', 'ok', 'ok']);

  await build.click();
  await expect(output).toContainText('Sync finished');
  // The status follows the 3-second stack poll.
  await expect(page.locator('.device-status')).toContainText('exited 0', { timeout: 10_000 });
  await expect(build).toBeEnabled();
});
