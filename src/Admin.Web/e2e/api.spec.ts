import { expect, test } from '@playwright/test';

// Against the fake platform (src/Admin.Host/Fakes/FakeGateway.cs, FakeKeycloak.cs): the product
// listing is anonymous and names "Walnut desk"; publishing is 401 with no token, 403 for browser
// and 200 for demo, as run-locally.md's "Publish a product" says of the real platform.
test('the api screen lists operations and sends as each identity', async ({ page }) => {
  await page.goto('/requests');

  await expect(page.locator('button.op')).toHaveCount(36);

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

// run-locally.md's "Attach a product image by hand" and "Saved delivery address", first calls: the BFF signs an upload
// for demo (catalog:write) and refuses browser, and the saved address is the caller's own (FakeGateway).
test('the api screen asks the BFF for an upload and saves an address', async ({ page }) => {
  await page.goto('/requests');

  await page.locator('button.op', { hasText: 'IssueProductImageUpload' }).click();
  await expect(page.getByLabel('Body')).toHaveValue(/"contentType": "image\/png"/);
  await page.getByLabel('Identity').selectOption('user:browser');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('403');

  await page.getByLabel('Identity').selectOption('user:demo');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');
  await expect(page.locator('.response pre.body')).toContainText('/product-images/uploads/');

  await page.locator('button.op', { hasText: 'PutSavedAddress' }).click();
  await page.getByLabel('Identity').selectOption('user:browser');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');
  await expect(page.locator('.response pre.body')).toContainText('"country": "GB"');
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

// run-locally.md, "Call the BFF directly (no gateway)": Admin:BffUrl is a surface the proxy admits, and the BFF checks the
// token itself, then answers its own routes, which are the gateway's /bff ones without the prefix (FakeBff, #136).
test('a call sent straight to the bff port is the bff own 401, then its 404 for a path it does not serve', async ({
  page,
}) => {
  await page.goto('/requests');

  await page.locator('button.op', { hasText: 'GetProducts' }).click();
  await page.getByLabel('URL').fill('http://localhost:5200/v1/no-such-path');
  await page.getByLabel('Identity').selectOption('anonymous');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('401');
  await expect(page.locator('.response pre.body')).toContainText('Unauthorized');

  await page.getByLabel('Identity').selectOption('user:demo');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('404');
  await expect(page.locator('.response pre.body')).toContainText('Not Found');
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

// The token clock reads what FakeKeycloak issued (its lifetime and its user's grants): a 403 for browser is explained by
// the grants it lists, and the token itself is never what the line shows.
test("the token clock shows the held token's time left and its grants, which explain a 403", async ({ page }) => {
  await page.goto('/requests');

  await page.locator('button.op', { hasText: 'GetStock' }).click();
  await page.getByLabel('Path productId').fill('0199a1b2-0000-7000-8000-00000000000a');
  await page.getByLabel('Identity').selectOption('user:browser');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('403');

  const clock = page.locator('.token-clock');
  await expect(clock).toContainText("browser's token:");
  await expect(clock).toContainText('Grants no permission.');

  await page.getByLabel('Identity').selectOption('user:demo');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');
  await expect(clock).toContainText(
    'Grants catalog:write, orders:write, orders:cancel, inventory:admin, payments:admin, orders:saga-read.',
  );
  await expect(clock).not.toContainText('.ey');
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

// FakePlatformHandler: the carrier simulator is the payment simulator's WireMock.Net image, and its request log
// answers empty until Shipping has despatched something. Shipping has no API, so this is its one by-hand read.
test("the carrier simulator's request log is Shipping's read, sent direct", async ({ page }) => {
  await page.goto('/requests');

  const carrier = page.locator('section', { has: page.locator('h3', { hasText: /^carrier$/ }) });
  await carrier.locator('button.op', { hasText: 'Requests' }).click();
  await page.getByLabel('Identity').selectOption('anonymous');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');
  await expect(page.locator('.response .body')).toContainText('[]');
});

// FakeOpenApi: Catalog's recorded document is served until it has a baseline, and from then on it has
// gained PublishProductCommand.description. Accepting is left to the host and unit specs: an accepted
// baseline lasts as long as the host does, and a reused host or a CI retry would then have no change to see.
test('a reload names what moved in a document since its baseline, with the action that accepts it', async ({
  page,
}) => {
  await page.goto('/requests');
  await expect(page.locator('button.op')).toHaveCount(36);

  await page.getByRole('button', { name: 'Reload' }).click();
  const changes = page.locator('.contract-changes');
  await expect(changes).toContainText('changed since');
  await expect(changes.locator('li')).toHaveText(['added field PublishProductCommand.description']);
  await expect(changes.getByRole('button', { name: 'Accept as baseline' })).toBeEnabled();
});
