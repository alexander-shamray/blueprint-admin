import { expect, test } from '@playwright/test';

// The fake logs -f output (src/Admin.Host/Fakes/FakePlatformScripts.cs) by Compose
// service: gateway 3, catalog-api 4, ordering-api 3, web-bff 1, rabbitmq 1. The fake
// replays only the services a follow names, as docker compose logs does.
test('following logs streams the selected services and highlights a correlation id', async ({ page }) => {
  await page.goto('/logs');

  await page.getByLabel('gateway', { exact: true }).check();
  await page.getByLabel('catalog-api', { exact: true }).check();
  await page.getByRole('button', { name: 'Follow' }).click();

  // gateway 3 + catalog-api 4.
  await expect(page.locator('.log .line')).toHaveCount(7);
  await expect(page.locator('.live')).toBeVisible();
  // Unselected services are absent; no gateway or catalog-api line names these three.
  await expect(page.locator('.log')).not.toContainText('ordering-api');
  await expect(page.locator('.log')).not.toContainText('web-bff');
  await expect(page.locator('.log')).not.toContainText('rabbitmq');

  // fake-corr-0001: the gateway's first proxying line and catalog-api's "Listed 20 products".
  await page.getByPlaceholder('correlation id').fill('fake-corr-0001');
  await expect(page.locator('.log .line.hit')).toHaveCount(2);

  // fake-corr-0002: the gateway's second proxying line and catalog-api's "Published" and
  // "Dispatched" lines; ordering-api's "Consumed" line carries it too but is not selected.
  await page.getByPlaceholder('filter text').fill('fake-corr-0002');
  await expect(page.locator('.log .line')).toHaveCount(3);

  await page.getByRole('button', { name: 'Stop' }).click();
  await expect(page.locator('.live')).toHaveCount(0);
});
