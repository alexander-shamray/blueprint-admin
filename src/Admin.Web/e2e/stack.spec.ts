import { expect, test } from '@playwright/test';

test('the stack screen lists the fake services and runs up', async ({ page }) => {
  await page.goto('/stack');

  await expect(page.locator('table.services tbody tr')).toHaveCount(24);
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
  await expect(rows.filter({ hasText: 'Gateway.Api' }).locator('td')).toHaveText(['Gateway.Api', '0.22', '0.0 %', '523 ms', '0.032', '0.033', '—', '—']);
  // Payments served no HTTP in the window, so its share is undefined and its refusals are zeros.
  await expect(rows.filter({ hasText: 'Payments.Api' }).locator('td')).toHaveText(['Payments.Api', '0', '—', '29 ms', '0', '0', '4750 ms', '—']);
});

// FakePlatformScripts replays this workstation's answers to the doctor's reads, with the host RabbitMQ that
// held 5672 and 15672 kept in the listener recording (WorkstationRecordings.Listeners).
test('the workstation panel is read before Up, green where fine and red where a port is held', async ({ page }) => {
  await page.goto('/stack');
  const rows = page.getByRole('region', { name: 'Workstation' }).locator('table.doctor tbody tr');

  await expect(rows).toHaveCount(7);
  await expect(rows.filter({ hasText: 'Docker' })).toHaveClass('state-Ok');
  await expect(rows.filter({ hasText: 'Docker' }).locator('td.state')).toHaveText('ok');
  await expect(rows.filter({ hasText: 'Ports' })).toHaveClass('state-Problem');
  await expect(rows.filter({ hasText: 'Ports' }).locator('td.detail')).toHaveText(
    'Held by another program, so Up cannot publish them: 5672, 15672 by erl (pid 7376).',
  );
});

// #81's audit: the panel's heading is a heading, its table names its columns and rows, and the job output is a log
// a keyboard can reach that is not announced line by line, beside a status that says how the job ended.
test('the workstation panel and the job output read correctly to assistive technology', async ({ page }) => {
  await page.goto('/stack');
  const workstation = page.getByRole('region', { name: 'Workstation' });

  await expect(workstation.getByRole('heading', { name: 'Workstation', level: 2 })).toBeVisible();
  await expect(workstation.getByRole('columnheader')).toHaveText(['Check', 'Verdict', 'What the reads said']);
  await expect(workstation.getByRole('rowheader', { name: 'Ports' })).toBeVisible();

  await page.getByRole('button', { name: 'Up' }).click();
  const log = page.getByRole('log', { name: 'Job output' });
  await expect(log).toHaveAttribute('aria-live', 'off');
  await expect(log).toContainText('exited 0');
  await expect(page.locator('app-output-pane').getByRole('status')).toHaveText('The job exited 0.');
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
