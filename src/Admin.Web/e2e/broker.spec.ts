import { expect, test } from '@playwright/test';

// Against the fake platform's recorded rabbitmqctl output (src/Admin.Host/Fakes/fixtures/rabbitmq-*.json):
// five queues, one of them an _error queue holding a message, ordering-catalog-events empty.
test('the broker screen lists queues, exchanges and permissions', async ({ page }) => {
  await page.goto('/broker');

  await expect(page.locator('table.queues tbody tr')).toHaveCount(5);
  await expect(page.locator('table.queues tbody tr.error-queue')).toHaveCount(1);
  await expect(page.locator('table.queues tbody tr.error-queue')).toContainText('[error] ordering-catalog-events_error');
  await expect(page.locator('app-drained-indicator')).toContainText('[drained] ordering-catalog-events');
  await expect(page.locator('table.exchanges tbody tr')).toHaveCount(15);
  await expect(page.locator('table.exchanges tbody tr', { hasText: 'ordering-fulfilment-saga_delay' })).toContainText('x-delayed-message');
  await expect(page.locator('table.permissions tbody tr')).toHaveCount(2);

  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.locator('table.queues tbody tr')).toHaveCount(5);
});
