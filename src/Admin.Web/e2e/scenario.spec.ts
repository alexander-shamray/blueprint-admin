import { expect, test } from '@playwright/test';

// Against the fake platform (src/Admin.Host/Fakes/FakeGateway.cs, FakeOrders.cs): publish and place-order answer
// with an id, the quote with a priced basket and cancel with 204; the recorded projection queue is empty, so the
// drain step passes on its first poll. The BFF's order read follows each order the fake placed, a step a second:
// confirmed, dispatched, delivered, or cancelled a second after a cancel. Other specs run the Scenario at the same
// time, so each run's order has an id of its own.
const WATCH = { timeout: 20_000 };

test('the deliver script orders and reads the order back until it is delivered, with a trace per call', async ({
  page,
}) => {
  await page.goto('/scenario');

  await expect(page.getByLabel('Run as')).toHaveValue('demo');
  await expect(page.getByLabel('Script')).toHaveValue('deliver');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps).toHaveCount(7);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(7, WATCH);
  await expect(steps.nth(0)).toContainText('0199a1b2-0000-7000-8000-000000000001');
  await expect(steps.nth(1).locator('app-drained-indicator')).toContainText('[drained]');
  // The order the run placed, which the fake numbers apart from any other run's.
  const placed = (await steps.nth(3).locator('.detail').textContent())!.match(/with id ([0-9a-f-]{36})/)![1];
  await expect(steps.nth(4).locator('.request')).toHaveText(`GET http://localhost:5000/bff/v1/orders/${placed}`);
  await expect(steps.nth(4).locator('.detail')).toContainText('The order read shows confirmed at');
  await expect(steps.nth(6).locator('.detail')).toContainText('The order read shows delivered at');
  await expect(steps.nth(6).locator('.detail')).toContainText('after it was placed');

  // The drain step asks the broker, not the platform's HTTP surface, so it has nothing to trace.
  await expect(page.locator('a.trace-step')).toHaveCount(6);
  await steps.nth(2).locator('a.trace-step').click();
  await expect(page).toHaveURL(/\/trace\/scenario-[0-9a-f]{8}-quote$/);
  await expect(page.locator('p.summary')).toContainText('-quote');
});

test('the cancel script cancels the placed order and reads it back cancelled', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('cancel');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps).toHaveCount(6);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(6, WATCH);
  await expect(steps.nth(4).locator('.status')).toHaveText('204');
  await expect(steps.nth(4).locator('.request')).toHaveText(
    /^POST http:\/\/localhost:5000\/api\/v1\/orders\/[0-9a-f-]{36}\/cancel$/,
  );
  await expect(steps.nth(5).locator('.detail')).toContainText('The order read shows cancelled at');
});

test('a user without the permissions stops the run at the publish', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Run as').selectOption('browser');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps.nth(0).locator('.state')).toHaveText('[failed]');
  await expect(steps.nth(0).locator('.status')).toHaveText('403');
  await expect(steps.locator('.state', { hasText: '[not run]' })).toHaveCount(6);
});
