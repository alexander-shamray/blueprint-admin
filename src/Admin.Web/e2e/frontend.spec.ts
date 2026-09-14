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
