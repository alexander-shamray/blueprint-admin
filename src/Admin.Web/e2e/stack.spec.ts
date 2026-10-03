import { expect, test } from '@playwright/test';

test('the stack screen lists the fake services and runs up', async ({ page }) => {
  await page.goto('/stack');

  await expect(page.locator('table.services tbody tr')).toHaveCount(13);
  await expect(page.locator('table.services tbody tr', { hasText: 'gateway' })).toContainText('healthy');
  await expect(page.locator('.reachability span.up')).toHaveCount(9);

  await page.getByRole('button', { name: 'Up' }).click();
  await expect(page.locator('app-output-pane pre')).toContainText('commerce-gateway-1  Healthy');
  await expect(page.locator('app-output-pane pre')).toContainText('exited 0');
});

// FakeGrafana answers each dashboard query with its live recording of 2026-10-03.
test('the stack screen shows every recorded golden signal per service', async ({ page }) => {
  await page.goto('/stack');
  const rows = page.getByRole('region', { name: 'Golden signals' }).locator('table.signals tbody tr');

  await expect(rows).toHaveCount(8);
  // The gateway answered 401s and 422s and no 5xx, which is a zero share, not a dash; it runs no commands.
  await expect(rows.filter({ hasText: 'Gateway.Api' }).locator('td')).toHaveText(['Gateway.Api', '0.22', '0.0 %', '523 ms', '0.03', '0.03', '—', '—']);
  // Payments served no HTTP in the window, so its share is undefined and its refusals are zeros.
  await expect(rows.filter({ hasText: 'Payments.Api' }).locator('td')).toHaveText(['Payments.Api', '0.00', '—', '29 ms', '0.00', '0.00', '4750 ms', '—']);
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

test('reset needs the typed confirmation, then runs down -v, up and the readiness wait as one job', async ({ page }) => {
  await page.goto('/stack');
  const reset = page.getByRole('button', { name: 'Reset' });

  await expect(reset).toBeDisabled();
  await page.getByPlaceholder('type: down -v').fill('down -v');
  await reset.click();

  const output = page.locator('app-output-pane pre');
  await expect(output).toContainText('Volume commerce_sql-data  Removed');
  await expect(output).toContainText('commerce-gateway-1  Healthy');
  await expect(output).toContainText('ready: gateway, catalog, ordering, bff, inventory, payments, keycloak, grafana');
  await expect(output).toContainText('exited 0');
});
