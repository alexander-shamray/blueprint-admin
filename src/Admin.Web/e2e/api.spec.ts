import { expect, test } from '@playwright/test';

// Against the fake platform (src/Admin.Host/Fakes/FakeGateway.cs, FakeKeycloak.cs): the product
// listing is anonymous and names "Walnut desk"; publishing is 401 with no token, 403 for browser
// and 200 for demo, as run-locally.md's "Publish a product" says of the real platform.
test('the api screen lists operations and sends as each identity', async ({ page }) => {
  await page.goto('/requests');

  await expect(page.locator('button.op')).toHaveCount(18);

  await page.locator('button.op', { hasText: 'GetProducts' }).click();
  await page.getByLabel('Identity').selectOption('anonymous');
  await page.getByLabel('Correlation id').fill('e2e-list-1');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');
  await expect(page.locator('.response .correlation')).toContainText('e2e-list-1');
  await expect(page.locator('.response pre.body')).toContainText('Walnut desk');

  await page.locator('button.op', { hasText: 'PublishProduct' }).click();
  await expect(page.getByLabel('Body')).toHaveValue(/Walnut desk/);
  await page.getByLabel('Correlation id').fill('');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('401');

  await page.getByLabel('Identity').selectOption('user:browser');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('403');

  await page.getByLabel('Identity').selectOption('user:demo');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');
  // run-locally.md: wait for ordering-catalog-events to drain before ordering; the fake queue is empty.
  await expect(page.locator('.response app-drained-indicator')).toContainText('[drained]');
  // Each send replaced the example's zero commandId with a fresh one.
  await expect(page.getByLabel('Body')).not.toHaveValue(/00000000-0000-0000-0000-000000000000/);

  await expect(page.locator('.history li')).toHaveCount(4);
  await expect(page.locator('.history li').first()).toContainText('200 PublishProduct as demo');
});

test('a wrong password is shown as keycloak refusing, not as a platform response', async ({ page }) => {
  await page.goto('/requests');

  await page.getByLabel('Identity').selectOption('custom');
  await page.getByLabel('Custom username').fill('demo');
  await page.getByLabel('Custom password').fill('wrong');
  await page.locator('button.op', { hasText: 'GetProducts' }).click();
  await page.getByRole('button', { name: 'Send' }).click();

  await expect(page.locator('.response.rejected')).toContainText('Keycloak refused the identity: 401');
  await expect(page.locator('.response.rejected pre.body')).toContainText('invalid_grant');
});

test('show token displays the demo permissions', async ({ page }) => {
  await page.goto('/requests');

  await page.getByLabel('Identity').selectOption('user:demo');
  await page.getByRole('button', { name: 'Show token' }).click();

  await expect(page.locator('.claims')).toContainText('orders:cancel');
});

// Spec §10: "a trace renders a timeline". The button carries the response's own correlation id to
// the Trace screen, which loads it from the route rather than from anything this screen holds.
test('trace this call opens the response correlation id on the trace screen', async ({ page }) => {
  await page.goto('/requests');

  await page.locator('button.op', { hasText: 'GetProducts' }).click();
  await page.getByLabel('Identity').selectOption('anonymous');
  await page.getByLabel('Correlation id').fill('e2e-trace-1');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');

  await page.locator('button.trace-call').click();

  await expect(page).toHaveURL(/\/trace\/e2e-trace-1$/);
  await expect(page.locator('p.summary')).toContainText('for e2e-trace-1');
  await expect(page.locator('table.timeline tbody tr').last().locator('td.kind')).toHaveText('[queued]');
});

// Inventory and Payments through the gateway (FakeGateway): the stock answer was recorded from the live
// service on 2026-10-03, and the payment follows the backend's PaymentView, since none exists without a
// running saga. Both routes need their admin permission, which demo holds and browser does not.
test('inventory stock and an order payment read as demo, and are refused for browser', async ({ page }) => {
  await page.goto('/requests');

  await page.locator('button.op', { hasText: 'GetStock' }).click();
  await page.getByLabel('Path productId').fill('0199a1b2-0000-7000-8000-00000000000a');
  await page.getByLabel('Identity').selectOption('user:demo');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');
  await expect(page.locator('.response .body')).toContainText('"available": 10');

  await page.getByLabel('Identity').selectOption('user:browser');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('403');

  await page.locator('button.op', { hasText: 'GetPayment' }).click();
  await page.getByLabel('Path orderId').fill('0199a1b2-0000-7000-8000-000000000002');
  await page.getByLabel('Identity').selectOption('user:demo');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');
  await expect(page.locator('.response .body')).toContainText('"status": "Authorised"');
});

// FakeOpenApi: Catalog's recorded document is served until it has a baseline, and from then on it has
// gained ProductSummaryDto.quantityAvailable. Accepting is left to the host and unit specs: an accepted
// baseline lasts as long as the host does, and a reused host or a CI retry would then have no change to see.
test('a reload names what moved in a document since its baseline, with the action that accepts it', async ({ page }) => {
  await page.goto('/requests');
  await expect(page.locator('button.op')).toHaveCount(18);

  await page.getByRole('button', { name: 'Reload' }).click();
  const changes = page.locator('.contract-changes');
  await expect(changes).toContainText('changed since');
  await expect(changes.locator('li')).toHaveText(['added field ProductSummaryDto.quantityAvailable']);
  await expect(changes.getByRole('button', { name: 'Accept as baseline' })).toBeEnabled();
});
