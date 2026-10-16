import { expect, test } from '@playwright/test';

// Against the fake platform (src/Admin.Host/Fakes/FakeGateway.cs, FakeOrders.cs): publish and place-order answer
// with an id, the quote with a priced basket and cancel with 204; the recorded projection queue is empty, so the
// drain step passes on its first poll. The BFF's order read follows each order the fake placed, a step a second:
// confirmed, dispatched, delivered, or cancelled a second after a cancel. Other specs run the Scenario at the same
// time, so each run's order and product have an id of its own.
const WATCH = { timeout: 20_000 };

test('the deliver script orders and reads the order back until it is delivered, with a trace per call', async ({
  page,
}) => {
  await page.goto('/scenario');

  await expect(page.getByLabel('Run as')).toHaveValue('demo');
  await expect(page.getByLabel('Script')).toHaveValue('deliver');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps).toHaveCount(7);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(7, WATCH);
  // The fake numbers each published product apart, as it does orders, so only the first is the recorded id.
  await expect(steps.nth(0).locator('.detail')).toContainText(/Answered 200 with id [0-9a-f-]{36}\./);
  await expect(steps.nth(1).locator('app-drained-indicator')).toContainText('[drained]');
  // The order the run placed, which the fake numbers apart from any other run's.
  const placed = (await steps.nth(3).locator('.detail').textContent())!.match(/with id ([0-9a-f-]{36})/)![1];
  await expect(steps.nth(4).locator('.request')).toHaveText(`GET http://localhost:5000/bff/v1/orders/${placed}`);
  await expect(steps.nth(4).locator('.detail')).toContainText('The order read shows confirmed at');
  await expect(steps.nth(6).locator('.detail')).toContainText('The order read shows delivered at');
  await expect(steps.nth(6).locator('.detail')).toContainText('after it was placed');

  // The drain step asks the broker, not the platform's HTTP surface, so it has nothing to trace.
  await expect(page.locator('a.trace-step')).toHaveCount(6);
  await steps.nth(2).locator('a.trace-step').click();
  await expect(page).toHaveURL(/\/trace\/scenario-[0-9a-f]{8}-quote$/);
  await expect(page.locator('p.summary')).toContainText('-quote');
});

test('the cancel script cancels the placed order and reads it back cancelled', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('cancel');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps).toHaveCount(6);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(6, WATCH);
  await expect(steps.nth(4).locator('.status')).toHaveText('204');
  await expect(steps.nth(4).locator('.request')).toHaveText(
    /^POST http:\/\/localhost:5000\/api\/v1\/orders\/[0-9a-f-]{36}\/cancel$/,
  );
  await expect(steps.nth(5).locator('.detail')).toContainText('The order read shows cancelled at');
});

// The fake scripts the end from the request, as the platform does from run-locally.md's cause
// (FakeOrders.EndOf): a price ending .01 is declined, stock below one is out of stock, a second after the place.
for (const [script, ended, stock] of [
  ['declined', 'declined', 'set to 10'],
  ['out-of-stock', 'out_of_stock', 'set to 0'],
] as const) {
  test(`the ${script} script sets its cause and reads the order back ${ended}`, async ({ page }) => {
    await page.goto('/scenario');

    await page.getByLabel('Script').selectOption(script);
    await page.getByRole('button', { name: 'Run' }).click();

    const steps = page.locator('ol.steps li.step');
    await expect(steps).toHaveCount(6);
    await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(6, WATCH);
    await expect(steps.nth(2).locator('.request')).toHaveText(
      /^PUT http:\/\/localhost:5000\/api\/v1\/inventory\/stock\/[0-9a-f-]{36}$/,
    );
    await expect(steps.nth(2).locator('.detail')).toContainText(stock);
    await expect(steps.nth(5).locator('.detail')).toContainText(`The order read shows ${ended} at`);
  });
}

// The carrier is scripted by the delivery postal code (FakeOrders.InTransitPostalCode): SIM-TRANSIT is collected and
// never delivered, so the order stays despatched, and a cancel asked of it is refused as the platform refuses it.
test('the in-transit script orders to SIM-TRANSIT and reads the order back despatched', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('in-transit');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps).toHaveCount(6);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(6, WATCH);
  await expect(steps.nth(3).locator('.detail')).toContainText('to the delivery postal code SIM-TRANSIT');
  await expect(steps.nth(5).locator('.detail')).toContainText('The order read shows dispatched at');
});

test('the already-shipped script is refused 422 order.already_shipped and reads the order back unchanged', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('already-shipped');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps).toHaveCount(8);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(8, WATCH);
  await expect(steps.nth(6).locator('.status')).toHaveText('422');
  await expect(steps.nth(6).locator('.detail')).toContainText('order.already_shipped');
  await expect(steps.nth(7).locator('.detail')).toContainText('The order read shows unchanged at');
});

// The timeout scripts hold a lease (spec §5.3). The fake stack remembers what it did (FakeStack): with Inventory stopped an
// order ends out of stock only when the saga's stock wait lapses, and a total ending .09 ends declined by the payment wait,
// each a few seconds with the short waits and minutes without. Every run ends with the lease restored.
for (const [script, ended, steps] of [
  ['stock-timeout', 'out_of_stock', 6],
  ['payment-timeout', 'declined', 8],
] as const) {
  test(`the ${script} script holds its lease, reads the order back ${ended}, and restores the stack`, async ({ page, request }) => {
    await page.goto('/scenario');

    await page.getByLabel('Script').selectOption(script);
    await expect(page.locator('p.lease-note')).toContainText('Everything is restored when the run ends');
    await page.getByRole('button', { name: 'Run' }).click();

    const list = page.locator('ol.steps li.step');
    await expect(list).toHaveCount(steps);
    await expect(list.locator('.state', { hasText: '[ok]' })).toHaveCount(steps, { timeout: 60_000 });
    await expect(list.first().locator('.detail')).toContainText('Held:');
    await expect(list.nth(steps - 2).locator('.detail')).toContainText(`The order read shows ${ended} at`);
    await expect(list.last().locator('.detail')).toHaveText('The stack is as the run found it.');

    const state = await (await request.get('/api/stack/lease')).json();
    expect(state.lease).toBeNull();
  });
}

test('Cancel ends a run that is waiting and still restores the stack', async ({ page, request }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('stock-timeout');
  await page.getByRole('button', { name: 'Run' }).click();
  // The lease is held from the first step on, and shown on the Stack screen while the run goes.
  await expect(page.locator('ol.steps li.step').first().locator('.state')).toHaveText('[ok]');

  await page.getByRole('button', { name: 'Cancel' }).click();

  const list = page.locator('ol.steps li.step');
  await expect(list.last().locator('.state')).toHaveText('[ok]', { timeout: 30_000 });
  await expect(list.last().locator('.detail')).toHaveText('The stack is as the run found it.');
  const state = await (await request.get('/api/stack/lease')).json();
  expect(state.lease).toBeNull();
});

test('a user without the permissions stops the run at the publish', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Run as').selectOption('browser');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps.nth(0).locator('.state')).toHaveText('[failed]');
  await expect(steps.nth(0).locator('.status')).toHaveText('403');
  await expect(steps.locator('.state', { hasText: '[not run]' })).toHaveCount(6);
});

// The fake store and edge (src/Admin.Host/Fakes/FakeObjectStore.cs) follow the platform's rules: the BFF signs a PUT, the
// store keeps what is signed, the publish attaches it, and the edge serves what was published, with nosniff.
test('the image script uploads, publishes with the key and reads the thumbnail back through the edge', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('image');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps).toHaveCount(3);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(3, WATCH);
  await expect(steps.nth(0).locator('.request')).toHaveText(
    /^PUT http:\/\/127\.0\.0\.1:8333\/product-images\/uploads\/[0-9a-f-]{36}\/[0-9a-f-]{36}\.png$/,
  );
  await expect(steps.nth(0).locator('.detail')).toContainText('the store answered 200 to the PUT');
  await expect(steps.nth(1).locator('.detail')).toContainText(/Answered 200 with id [0-9a-f-]{36}\./);
  await expect(steps.nth(2).locator('.request')).toHaveText(
    /^HEAD http:\/\/127\.0\.0\.1:9000\/products\/[0-9a-f-]{36}\/[0-9a-f-]{36}\.png$/,
  );
  await expect(steps.nth(2).locator('.detail')).toContainText('The edge answered 200 with an image and nosniff.');
});

test('the image script stops at the signing for a user who may not publish', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('image');
  await page.getByLabel('Run as').selectOption('browser');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps.nth(0).locator('.state')).toHaveText('[failed]');
  await expect(steps.nth(0).locator('.status')).toHaveText('403');
  await expect(steps.locator('.state', { hasText: '[not run]' })).toHaveCount(2);
});
