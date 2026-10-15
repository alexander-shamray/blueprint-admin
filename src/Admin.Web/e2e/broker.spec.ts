import { expect, test } from '@playwright/test';

// Against the fake platform's recorded rabbitmqctl output (src/Admin.Host/Fakes/fixtures/rabbitmq-*.json):
// twenty queues, an _error queue and a _skipped queue each holding a message, ordering-catalog-events empty; 47 exchanges;
// a permission row per service user.
// The BFF projection panel against the fake platform's recordings of run-locally.md's lines: the repair finds every
// row already in the BFF's inbox, and the rebuild stops the BFF, replays the 24 events, starts it, and sees the queue empty.
test('the projection panel states its limits, then runs a repair and a confirmed rebuild', async ({ page }) => {
  await page.goto('/broker');
  const panel = page.getByRole('region', { name: 'BFF order projection' });

  await expect(panel.locator('ul.limits li')).toHaveCount(4);
  await expect(panel.locator('ul.limits')).toContainText('A replay reaches back one outbox window and no further');
  await expect(panel.locator('.depth')).toContainText('[drained] bff-order-events is empty.');

  await panel.getByRole('button', { name: 'Repair' }).click();
  const log = panel.getByRole('log', { name: 'Job output' });
  await expect(log).toContainText('Skipped 24 event(s) the BFF');
  await expect(log).toContainText('bff-order-events: 0 messages (drained). Trust the projection now.');
  await expect(log).toContainText('exited 0');
  await expect(log).not.toContainText('web-bff');

  const rebuild = panel.getByRole('button', { name: 'Rebuild' });
  await expect(rebuild).toBeDisabled();
  await panel.getByLabel('Type bff-replay --reset to confirm a rebuild').fill('bff-replay --reset');
  await expect(rebuild).toBeEnabled();
  await rebuild.click();

  await expect(log).toContainText('Sent 24 event(s) to bff-order-events.');
  await expect(log).toContainText('exited 0');
  const text = (await log.textContent()) ?? '';
  expect(text.indexOf('stop web-bff')).toBeGreaterThan(-1);
  expect(text.indexOf('Reset: the order rows')).toBeGreaterThan(text.indexOf('stop web-bff'));
  expect(text.indexOf('start web-bff')).toBeGreaterThan(text.indexOf('Reset: the order rows'));
});

test('the broker screen lists queues, exchanges and permissions', async ({ page }) => {
  await page.goto('/broker');

  await expect(page.locator('table.queues tbody tr')).toHaveCount(20);
  await expect(page.locator('table.queues tbody tr.error-queue')).toHaveCount(1);
  await expect(page.locator('table.queues tbody tr.error-queue')).toContainText('[error] ordering-catalog-events_error');
  await expect(page.locator('table.queues tbody tr.skipped-queue')).toHaveCount(1);
  await expect(page.locator('table.queues tbody tr.skipped-queue')).toContainText('[skipped] bff-order-events_skipped');
  await expect(page.locator('table.queues tbody tr.skipped-queue')).toContainText("the BFF's order projection");
  await expect(page.locator('table.queues tbody tr.skipped-queue a')).toHaveAttribute('href', /\/docs\/runbooks\/skipped-queue\.md$/);
  await expect(page.locator('table.queues tbody tr.error-queue a')).toHaveAttribute('href', /\/docs\/runbooks\/error-queue\.md$/);
  await expect(page.locator('app-drained-indicator')).toContainText('[drained] ordering-catalog-events');
  await expect(page.locator('table.exchanges tbody tr')).toHaveCount(56);
  await expect(page.locator('table.exchanges tbody tr', { hasText: 'ordering-fulfilment-saga_delay' })).toContainText('x-delayed-message');
  await expect(page.locator('table.permissions tbody tr')).toHaveCount(9);

  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.locator('table.queues tbody tr')).toHaveCount(20);

  const reread = page.waitForRequest('**/api/broker/permissions');
  await page.getByLabel('Refresh every 5 s').check();
  await reread;
});
