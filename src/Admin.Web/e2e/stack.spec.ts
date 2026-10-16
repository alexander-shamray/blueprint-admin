import { expect, test } from '@playwright/test';

test('the stack screen lists the fake services and runs up', async ({ page }) => {
  await page.goto('/stack');

  await expect(page.locator('table.services tbody tr')).toHaveCount(32);
  // As recorded: SQL Server has a healthcheck and the carrier simulator has none, so ps gives it a state and no health.
  const service = (name: string) =>
    page.locator('table.services tbody tr').filter({ has: page.locator('td:first-child', { hasText: new RegExp(`^${name}$`) }) });
  await expect(service('sql')).toContainText('healthy');
  await expect(service('carrier-simulator')).toContainText('running');
  await expect(service('carrier-simulator')).not.toContainText('healthy');
  await expect(page.locator('.reachability span.up')).toHaveCount(12);
  // The store and the edge are surfaces of their own: an Up that leaves the image store unprovisioned looks green otherwise.
  await expect(page.locator('.reachability span.up', { hasText: 'object-store' })).toHaveCount(1);
  await expect(page.locator('.reachability span.up', { hasText: 'image-edge' })).toHaveCount(1);
  // The one-shot that makes the bucket is shown like a migrator: exited, with its code.
  await expect(service('object-store-init')).toContainText('exited');

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

// FakeGrafana answers the rules read with a hand-shaped recording: one rule firing, one pending, the rest quiet.
// The waiting rules are the backend's awaiting-signal.yaml, which no Prometheus lists, so the host adds them.
test('the stack screen lists a firing, a pending and a quiet rule, and the waiting ones apart', async ({ page }) => {
  await page.goto('/stack');
  const alerts = page.getByRole('region', { name: 'Alerts' });
  const loaded = alerts.locator('table.alerts tbody tr');

  await expect(loaded).toHaveCount(5);
  await expect(loaded.first().locator('td')).toHaveText(['ErrorQueueDepth', 'firing', 'page', '1']);
  await expect(loaded.filter({ hasText: 'Latency' })).toHaveClass('state-pending');
  await expect(loaded.filter({ hasText: 'ErrorRateGateway' })).toHaveClass('state-inactive');

  const waiting = alerts.locator('table.waiting tbody tr');
  await expect(waiting).toHaveCount(3);
  await expect(waiting.filter({ hasText: 'StuckSaga' }).locator('td')).toHaveText(['StuckSaga', 'ordering_saga_oldest_unfinalised_age_seconds']);
  await expect(alerts.locator('table.alerts')).not.toContainText('StuckSaga');
});

// FakePlatformScripts replays this workstation's answers to the doctor's reads, with the host RabbitMQ that
// held 5672 and 15672 kept in the listener recording (WorkstationRecordings.Listeners).
test('the workstation panel is read before Up, green where fine and red where a port is held', async ({ page }) => {
  await page.goto('/stack');
  const rows = page.getByRole('region', { name: 'Workstation' }).locator('table.doctor tbody tr');

  await expect(rows).toHaveCount(9);
  await expect(rows.filter({ hasText: 'Python' })).toHaveClass('state-Ok');
  await expect(rows.filter({ hasText: 'Object store' })).toHaveClass('state-Ok');
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
  await expect(output).toContainText('ready: gateway, catalog, ordering, bff, inventory, payments, privacy, object-store, image-edge, keycloak, grafana');
  await expect(output).toContainText('exited 0');
});
