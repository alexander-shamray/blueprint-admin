import { expect, test } from '@playwright/test';

// Against the fake platform's recorded rabbitmqctl output (src/Admin.Host/Fakes/fixtures/rabbitmq-*.json):
// twenty queues, an _error queue and a _skipped queue each holding a message, ordering-catalog-events empty; 56 exchanges;
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

// The dead-letter workbench against the fake tool (src/Admin.Host/Fakes/DeadLetterRecordings.cs): the error queue holds one
// faulted PriceChanged; each run sets the operator's password and clears it, and the real run needs the typed flag.
test('an error queue opens into its message, dry-runs a replay, then replays it with the typed confirmation', async ({ page }) => {
  await page.goto('/broker');
  await page.getByRole('button', { name: 'Open the messages on ordering-catalog-events_error' }).click();

  const panel = page.getByRole('region', { name: 'Dead letters on ordering-catalog-events_error' });
  const message = panel.locator('article.dead-letter');
  await expect(message).toHaveCount(1);
  await expect(message).toContainText('System.InvalidOperationException: Product 0199a3f0-4b1c-7d2e-9f10-2a3b4c5d6e7f has no row in the price projection');
  await expect(message).toContainText('urn:message:Common.Contracts.Catalog.V1:PriceChanged');
  await expect(message.getByRole('link', { name: 'fake-corr-0002' })).toHaveAttribute('href', '/trace/fake-corr-0002');
  await expect(panel.getByRole('button', { name: 'Replay for real' })).toHaveCount(0);

  await message.getByRole('button', { name: 'Dry-run replay' }).click();
  const log = panel.getByRole('log', { name: 'Job output' });
  await expect(log).toContainText('would-replay');
  await expect(log).toContainText('-> ordering-catalog-events');
  await expect(log).toContainText('dry run: nothing left ordering-catalog-events_error');
  await expect(log).toContainText('clear_password dead-letter-operator');
  await expect(log).toContainText('exited 0');

  const execute = panel.getByRole('button', { name: 'Replay for real' });
  await expect(execute).toBeDisabled();
  await panel.getByLabel('Type --execute to replay for real').fill('--execute');
  await expect(execute).toBeEnabled();
  await execute.click();

  await expect(log).toContainText('replayed');
  await expect(log).toContainText('--execute --record');
  await expect(log).toContainText('exited 0');
  await expect(log).not.toContainText('would-replay');
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
