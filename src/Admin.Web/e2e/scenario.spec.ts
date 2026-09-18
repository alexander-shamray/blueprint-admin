import { expect, test } from '@playwright/test';

// Against the fake platform (src/Admin.Host/Fakes/FakeGateway.cs): publish and place-order answer with
// a recorded id, the quote with a priced basket and cancel with 204; the recorded projection queue is
// empty, so the drain step passes on its first poll.
test('the scenario screen runs publish to cancel with a trace per call', async ({ page }) => {
  await page.goto('/scenario');

  await expect(page.getByLabel('Run as')).toHaveValue('demo');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps).toHaveCount(5);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(5);
  await expect(steps.nth(0)).toContainText('0199a1b2-0000-7000-8000-000000000001');
  await expect(steps.nth(1).locator('app-drained-indicator')).toContainText('[drained]');
  await expect(steps.nth(4).locator('.status')).toHaveText('204');
  await expect(steps.nth(4).locator('.request')).toContainText(
    '/api/v1/orders/0199a1b2-0000-7000-8000-000000000002/cancel',
  );

  // The drain step asks the broker, not the platform's HTTP surface, so it has nothing to trace.
  await expect(page.locator('a.trace-step')).toHaveCount(4);
  await steps.nth(2).locator('a.trace-step').click();
  await expect(page).toHaveURL(/\/trace\/scenario-[0-9a-f]{8}-quote$/);
  await expect(page.locator('p.summary')).toContainText('-quote');
});

test('a user without the permissions stops the run at the publish', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Run as').selectOption('browser');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps.nth(0).locator('.state')).toHaveText('[failed]');
  await expect(steps.nth(0).locator('.status')).toHaveText('403');
  await expect(steps.locator('.state', { hasText: '[not run]' })).toHaveCount(4);
});
