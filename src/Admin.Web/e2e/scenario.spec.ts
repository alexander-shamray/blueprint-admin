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
  await expect(steps).toHaveCount(9);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(9, WATCH);
  // The fake numbers each published product apart, as it does orders, so only the first is the recorded id.
  await expect(steps.nth(0).locator('.detail')).toContainText(/Answered 200 with id [0-9a-f-]{36}\./);
  await expect(steps.nth(1).locator('app-drained-indicator')).toContainText('[drained]');
  // The order the run placed, which the fake numbers apart from any other run's.
  await expect(steps.nth(2).locator('.request')).toHaveText(/^PUT http:\/\/localhost:5000\/api\/v1\/inventory\/stock\/[0-9a-f-]{36}$/);
  await expect(steps.nth(2).locator('.detail')).toContainText('stock on hand set to 10');
  const placed = (await steps.nth(4).locator('.detail').textContent())!.match(/with id ([0-9a-f-]{36})/)![1];
  await expect(steps.nth(5).locator('.request')).toHaveText(`GET http://localhost:5000/bff/v1/orders/${placed}`);
  await expect(steps.nth(5).locator('.detail')).toContainText('The order read shows confirmed at');
  await expect(steps.nth(7).locator('.detail')).toContainText('The order read shows delivered at');
  await expect(steps.nth(7).locator('.detail')).toContainText('after it was placed');
  // The fake sink answers for the run's own order: one message for each event it raised (FakeOrders.Mail), read through the host.
  await expect(steps.nth(8).locator('.request')).toHaveText('GET /api/mail/messages');
  await expect(steps.nth(8).locator('.detail')).toContainText(
    'OrderPlaced (order-placed) ×1, OrderConfirmed (order-confirmed) ×1, ShipmentDispatched (shipment-dispatched) ×1, ShipmentDelivered (shipment-delivered) ×1',
  );

  // The drain step asks the broker, not the platform's HTTP surface, so it has nothing to trace.
  await expect(page.locator('a.trace-step')).toHaveCount(7);
  await steps.nth(3).locator('a.trace-step').click();
  await expect(page).toHaveURL(/\/trace\/scenario-[0-9a-f]{8}-quote$/);
  await expect(page.locator('p.summary')).toContainText('-quote');
});

// The fake never sends a message twice, so the doubled one is the sink's answer altered in flight: the confirmed message listed
// again, as a redelivery that sends twice would leave it.
test('a doubled message is shown as two and fails the mail step', async ({ page }) => {
  await page.route('**/api/mail/messages', async (route) => {
    const response = await route.fetch();
    const view = await response.json();
    const confirmed = view.messages.find((m: { messageId: string }) => m.messageId.includes('.order-confirmed@'));
    view.messages.push({ ...confirmed, id: `${confirmed.id}-again` });
    await route.fulfill({ response, json: view });
  });
  await page.goto('/scenario');

  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps.locator('.state', { hasText: '[failed]' })).toHaveCount(1, WATCH);
  await expect(steps.nth(8).locator('.state')).toHaveText('[failed]');
  await expect(steps.nth(8).locator('.detail')).toContainText(
    'A message was sent more than once: OrderConfirmed (order-confirmed) sent 2 times.',
  );
});

test('the cancel script cancels the placed order and reads it back cancelled', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('cancel');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps).toHaveCount(7);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(7, WATCH);
  await expect(steps.nth(5).locator('.status')).toHaveText('204');
  await expect(steps.nth(5).locator('.request')).toHaveText(
    /^POST http:\/\/localhost:5000\/api\/v1\/orders\/[0-9a-f-]{36}\/cancel$/,
  );
  await expect(steps.nth(6).locator('.detail')).toContainText('The order read shows cancelled at');
});

// The fake scripts the end from the request, as the platform does from run-locally.md's cause
// (FakeOrders.EndOf): a price ending .01 is declined, stock never set or below one is out of stock, a second after the place.
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
  await expect(steps).toHaveCount(7);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(7, WATCH);
  await expect(steps.nth(4).locator('.detail')).toContainText('to the delivery postal code SIM-TRANSIT');
  await expect(steps.nth(6).locator('.detail')).toContainText('The order read shows dispatched at');
});

// The saga strip beside the steps (spec §5.13): the order's state as the BFF last read it, the time each transition took
// from the platform's own stamps, and what it is: the last read, and the only stuck-saga detector a workstation has.
test('the saga strip follows a saga that advances to delivered and says it is the last read', async ({ page }) => {
  await page.goto('/scenario');
  await expect(page.locator('aside.saga .saga-none')).toBeVisible();

  await page.getByRole('button', { name: 'Run' }).click();

  const strip = page.locator('aside.saga');
  await expect(strip.locator('.saga-status')).toHaveText('delivered', WATCH);
  await expect(strip.locator('.transitions li')).toHaveCount(3);
  await expect(strip.locator('.transitions li').first()).toContainText('placed → confirmed:');
  await expect(strip.locator('.transitions li').last()).toContainText('dispatched → delivered:');
  await expect(strip.locator('.saga-read')).toContainText('Not reading now: this is the last state read');
  await expect(strip.locator('.saga-caveat')).toContainText('only stuck-saga detector a workstation has');
  await expect(strip.locator('.saga-stalled')).toHaveCount(0);
  // The saga's own word beside the buyer's (the fake's saga finalises at despatch, so it has no row by delivery): an agreeing pair.
  await expect(strip.locator('.saga-columns th[scope="col"]', { hasText: 'Saga (Ordering)' })).toBeVisible();
  await expect(strip.locator('.saga-columns .saga-state')).toContainText('no row');
  await expect(strip.locator('.saga-columns .saga-status-cell')).toHaveText('delivered');
  await expect(strip.locator('.saga-pair-agree')).toContainText('The two agree');
  await expect(strip.locator('.saga-pair-differs')).toHaveCount(0);
});

// The platform has no fake saga that lies, so the saga read is answered by the route: Ordering still waiting on stock while
// the BFF's read has the order delivered is a pair §9.6 does not allow, and the strip says so in words, not by colour alone.
test('the saga strip shows a saga still awaiting stock beside a delivered order as a disagreement', async ({ page }) => {
  await page.route('**/api/proxy', async (route) => {
    const sent = route.request().postDataJSON() as { method?: string; url?: string; correlationId?: string };
    if (sent.method === 'GET' && /\/api\/v1\/orders\/[0-9a-f-]{36}\/saga$/.test(sent.url ?? '')) {
      await route.fulfill({
        json: {
          outcome: 'responded',
          status: 200,
          headers: {},
          body: JSON.stringify({
            orderId: sent.url!.split('/').slice(-2)[0],
            currentState: 'AwaitingStock',
            startedAt: '2026-10-05T12:00:00Z',
            cancelReason: null,
          }),
          bodyTruncated: false,
          bodyError: null,
          elapsedMs: 1,
          correlationId: sent.correlationId ?? null,
        },
      });
    } else {
      await route.continue();
    }
  });
  await page.goto('/scenario');
  await page.getByRole('button', { name: 'Run' }).click();

  const strip = page.locator('aside.saga');
  await expect(strip.locator('.saga-status')).toHaveText('delivered', WATCH);
  await expect(strip.locator('.saga-columns .saga-state')).toHaveText('AwaitingStock');
  const differs = strip.locator('.saga-pair-differs');
  await expect(differs).toContainText('[disagree]');
  await expect(differs).toContainText("a pair the backend's table does not allow");
  // A disagreement is shown, never a reason to stop the run: every step still passed.
  await expect(page.locator('ol.steps li.step .state', { hasText: '[ok]' })).toHaveCount(9, WATCH);
});

test('the saga strip shows a saga that stops at despatched as despatched, with no delivery it never read', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('in-transit');
  await page.getByRole('button', { name: 'Run' }).click();

  const strip = page.locator('aside.saga');
  await expect(strip.locator('.saga-status')).toHaveText('dispatched', WATCH);
  await expect(page.getByRole('button', { name: 'Export Markdown' })).toBeEnabled(WATCH);
  await expect(strip.locator('.transitions li').last()).toContainText('confirmed → dispatched:');
  await expect(strip.locator('.transitions li', { hasText: 'delivered' })).toHaveCount(0);
});

// No fake saga stands still for the strip's stall time inside a watch, so the order read is held at confirmed by the route
// and the page's clock is run forward: the console's own polling and its clock are what the stall mark is measured by.
test('the saga strip marks a saga that stands still at confirmed as stalled, in words', async ({ page }) => {
  const placed = '2026-10-05T12:00:00Z';
  const confirmed = '2026-10-05T12:00:01Z';
  await page.route('**/api/proxy', async (route) => {
    const sent = route.request().postDataJSON() as { method?: string; url?: string; correlationId?: string };
    if (sent.method === 'GET' && /\/bff\/v1\/orders\/[0-9a-f-]{36}$/.test(sent.url ?? '')) {
      const body = JSON.stringify({
        orderId: sent.url!.split('/').pop(),
        status: 'confirmed',
        timeline: { placed, confirmed, dispatched: null, delivered: null, cancelled: null },
        asOf: confirmed,
      });
      await route.fulfill({
        json: {
          outcome: 'responded',
          status: 200,
          headers: {},
          body,
          bodyTruncated: false,
          bodyError: null,
          elapsedMs: 1,
          correlationId: sent.correlationId ?? null,
        },
      });
    } else {
      await route.continue();
    }
  });
  await page.clock.install();
  await page.goto('/scenario');
  await page.getByRole('button', { name: 'Run' }).click();

  const stalled = page.locator('aside.saga .saga-stalled');
  for (let i = 0; i < 200 && !(await stalled.isVisible()); i++) {
    await page.clock.runFor(1000);
    await page.waitForTimeout(25);
  }

  await expect(stalled).toBeVisible();
  await expect(stalled).toContainText('[stalled] The status has stood at confirmed for');
  await expect(page.locator('aside.saga .saga-status')).toHaveText('confirmed');
  await expect(page.locator('aside.saga .saga-read')).toContainText('Still reading.');
});

test('a finished run exports as one Markdown document, scrubbed by the host, with its sections', async ({ page }) => {
  await page.goto('/scenario');
  const exportButton = page.getByRole('button', { name: 'Export Markdown' });
  await expect(exportButton).toBeDisabled();

  await page.getByRole('button', { name: 'Run' }).click();
  await expect(exportButton).toBeEnabled(WATCH);
  const [download] = await Promise.all([page.waitForEvent('download'), exportButton.click()]);

  expect(download.suggestedFilename()).toMatch(/^scenario-[0-9a-f]{8}\.md$/);
  const stream = await download.createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(chunk as Buffer);
  const md = Buffer.concat(chunks).toString('utf-8');
  for (const heading of [
    '## Operator transcript',
    '## Steps',
    '## The order, as the platform reported it',
    '## Correlation ids',
    '## Trace timeline',
    '## Queue depths',
    '## Mail the run caused',
  ]) {
    expect(md).toContain(heading);
  }
  expect(md).toContain('- Outcome: every step succeeded');
  expect(md).toContain('## Mail the run caused');
  expect(md).toContain('ok: One message for each event the order raised: OrderPlaced (order-placed) ×1');
  expect(md).not.toMatch(/eyJ[A-Za-z0-9_-]{10,}\./);
});

test('the already-shipped script is refused 422 order.already_shipped and reads the order back unchanged', async ({ page }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('already-shipped');
  await page.getByRole('button', { name: 'Run' }).click();

  const steps = page.locator('ol.steps li.step');
  await expect(steps).toHaveCount(9);
  await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(9, WATCH);
  await expect(steps.nth(7).locator('.status')).toHaveText('422');
  await expect(steps.nth(7).locator('.detail')).toContainText('order.already_shipped');
  await expect(steps.nth(8).locator('.detail')).toContainText('The order read shows unchanged at');
});

// The four carrier outcomes the order read cannot tell apart are read from the simulator's own request log
// (FakeOrders.CarrierLog, answered by FakeCarrier): the order goes to the code that scripts the carrier, the log shows
// what the carrier saw, and the order read is held at confirmed.
for (const [script, code, saw] of [
  ['carrier-refused', 'SIM-REFUSED', 'The carrier refused the booking with 422.'],
  ['carrier-down', 'SIM-DOWN', 'each answered 503'],
  ['carrier-slow', 'SIM-SLOW', 'each answered 201 after Shipping had stopped waiting'],
  ['carrier-strange', 'SIM-STRANGE', 'its feed has been polled'],
] as const) {
  test(`the ${script} script orders to ${code} and reads the carrier's log and the order back`, async ({ page }) => {
    await page.goto('/scenario');

    await page.getByLabel('Script').selectOption(script);
    await page.getByRole('button', { name: 'Run' }).click();

    const steps = page.locator('ol.steps li.step');
    await expect(steps).toHaveCount(8);
    await expect(steps.locator('.state', { hasText: '[ok]' })).toHaveCount(8, { timeout: 60_000 });
    await expect(steps.nth(4).locator('.detail')).toContainText(`to the delivery postal code ${code}`);
    await expect(steps.nth(6).locator('.request')).toHaveText('POST http://localhost:5191/__admin/requests/find');
    await expect(steps.nth(6).locator('.detail')).toContainText(saw);
    await expect(steps.nth(7).locator('.detail')).toContainText('The order read shows held at');
  });
}

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

// The despatch timeout ends in a review row no order read shows, so the script reads the line Ordering logs when it raises one
// (FakeOrders.ReviewFlags, answered by FakeGrafana.Loki): an order to SIM-STRANGE under the short waits is flagged a few seconds
// after the place, and the order read is still confirmed.
test('the despatch-timeout script reads the review Ordering raised, finds the order still confirmed, and restores the stack', async ({ page, request }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('despatch-timeout');
  await page.getByRole('button', { name: 'Run' }).click();

  const list = page.locator('ol.steps li.step');
  await expect(list).toHaveCount(10);
  await expect(list.locator('.state', { hasText: '[ok]' })).toHaveCount(10, { timeout: 60_000 });
  await expect(list.nth(7).locator('.request')).toHaveText(/^GET \/api\/review\/[0-9a-f-]{36}$/);
  await expect(list.nth(7).locator('.detail')).toContainText('flagged for review: not_despatched');
  await expect(list.nth(8).locator('.detail')).toContainText('The order read shows held at');
  await expect(list.last().locator('.detail')).toHaveText('The stack is as the run found it.');

  const state = await (await request.get('/api/stack/lease')).json();
  expect(state.lease).toBeNull();
});

// The release timeout needs Inventory stopped after its stock has reserved, so the lease takes that change while it is held
// (POST /api/stack/lease/changes). A .09 order cancelled before its payment wait lapses is cancelled by the customer, and with
// Inventory stopped it is flagged stock_not_released (FakeOrders.Cancel, ReviewFlags).
test('the release-timeout script stops Inventory once the stock is reserved, cancels, reads the review, and restores the stack', async ({ page, request }) => {
  await page.goto('/scenario');

  await page.getByLabel('Script').selectOption('release-timeout');
  await page.getByRole('button', { name: 'Run' }).click();

  const list = page.locator('ol.steps li.step');
  await expect(list).toHaveCount(12);
  await expect(list.locator('.state', { hasText: '[ok]' })).toHaveCount(12, { timeout: 60_000 });
  await expect(list.nth(6).locator('.detail')).toContainText('Reserved');
  await expect(list.nth(9).locator('.detail')).toContainText('The order read shows cancelled at');
  await expect(list.nth(10).locator('.detail')).toContainText('flagged for review: stock_not_released');
  await expect(list.last().locator('.detail')).toHaveText('The stack is as the run found it.');

  const state = await (await request.get('/api/stack/lease')).json();
  expect(state.lease).toBeNull();
});

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
  await expect(steps.locator('.state', { hasText: '[not run]' })).toHaveCount(8);
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
