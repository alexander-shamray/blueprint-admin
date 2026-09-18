import { expect, test } from '@playwright/test';

test('the stack screen lists the fake services and runs up', async ({ page }) => {
  await page.goto('/stack');

  await expect(page.locator('tbody tr')).toHaveCount(13);
  await expect(page.locator('tbody tr', { hasText: 'gateway' })).toContainText('healthy');
  await expect(page.locator('.reachability span.up')).toHaveCount(7);

  await page.getByRole('button', { name: 'Up' }).click();
  await expect(page.locator('app-output-pane pre')).toContainText('commerce-gateway-1  Healthy');
  await expect(page.locator('app-output-pane pre')).toContainText('exited 0');
});

test('the stack screen shows the recorded golden signals per service', async ({ page }) => {
  await page.goto('/stack');
  const signals = page.getByRole('region', { name: 'Golden signals' }).locator('li');

  await expect(signals).toHaveCount(3);
  await expect(signals.filter({ hasText: 'Gateway.Api' })).toContainText('2.40 req/s');
  await expect(signals.filter({ hasText: 'Gateway.Api' })).toContainText('5.0 % 5xx');
  await expect(signals.filter({ hasText: 'Catalog.Api' })).toContainText('p99 48 ms');
});

test('wiping volumes needs the typed confirmation', async ({ page }) => {
  await page.goto('/stack');
  const wipe = page.getByRole('button', { name: 'Down and wipe' });

  await expect(wipe).toBeDisabled();
  await expect(page.getByPlaceholder('type: down -v')).toHaveAccessibleName('Type down -v to confirm wiping volumes');
  await page.getByPlaceholder('type: down -v').fill('down -v');
  await expect(wipe).toBeEnabled();

  await wipe.click();
  await expect(page.locator('app-output-pane pre')).toContainText('Volume commerce_sql-data  Removed');
});
