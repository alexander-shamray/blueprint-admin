import { expect, test } from '@playwright/test';

// The map against the fake platform: Ordering's section carries its gateway route and the queue the fake broker lists for it,
// with that queue's depth; the fake's broker and the tables agree, so nothing is listed as a disagreement.
test('the map joins a service to its route and its queue with the depth the broker reports', async ({ page }) => {
  await page.goto('/map');
  const ordering = page.getByRole('region', { name: 'Service ordering' });

  await expect(ordering.locator('table.routes')).toContainText('/api/v1/orders/');
  const error = ordering.locator('table.queues tr.error-queue');
  await expect(error).toContainText('ordering-catalog-events_error');
  await expect(error.locator('td.depth')).toHaveText('1');
  await expect(page.getByRole('region', { name: 'Service shipping' })).toContainText('worker, no API of its own');
  await expect(page.getByRole('region', { name: 'Disagreements' })).toHaveCount(0);
});
