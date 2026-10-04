import { expect, test } from '@playwright/test';

// Against the fake platform's recorded rabbitmqctl output (src/Admin.Host/Fakes/fixtures/rabbitmq-*.json):
// thirteen queues, one of them an _error queue holding a message, ordering-catalog-events empty; 47 exchanges;
// a permission row per service user.
test('the broker screen lists queues, exchanges and permissions', async ({ page }) => {
  await page.goto('/broker');

  await expect(page.locator('table.queues tbody tr')).toHaveCount(13);
  await expect(page.locator('table.queues tbody tr.error-queue')).toHaveCount(1);
  await expect(page.locator('table.queues tbody tr.error-queue')).toContainText('[error] ordering-catalog-events_error');
  await expect(page.locator('app-drained-indicator')).toContainText('[drained] ordering-catalog-events');
  await expect(page.locator('table.exchanges tbody tr')).toHaveCount(47);
  await expect(page.locator('table.exchanges tbody tr', { hasText: 'ordering-fulfilment-saga_delay' })).toContainText('x-delayed-message');
  await expect(page.locator('table.permissions tbody tr')).toHaveCount(7);

  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.locator('table.queues tbody tr')).toHaveCount(13);

  const reread = page.waitForRequest('**/api/broker/permissions');
  await page.getByLabel('Refresh every 5 s').check();
  await reread;
});
