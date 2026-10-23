import { expect, test } from '@playwright/test';

// Against the fake host (FakePlatformScripts): the lease's stop, its one restore line and the recorded ps, which keeps
// Inventory running, so the restore's verification reads true. The host lives for the whole run and one worker drives it,
// so every test starts and ends with no lease held: a held one would refuse the other specs' Up and Down with 409.
test.beforeEach(async ({ request }) => {
  const restored = await request.post('/api/stack/lease/restore');
  expect(restored.status()).toBe(200);
});

test.afterEach(async ({ request }) => {
  await request.post('/api/stack/lease/restore');
});

test('a held lease shows on the stack screen and Restore now undoes it', async ({ page, request }) => {
  const held = await request.post('/api/stack/lease', {
    data: { changes: ['stop-inventory'], holdSeconds: 120, label: 'smoke' },
  });
  expect(held.status()).toBe(200);

  await page.goto('/stack');
  const banner = page.locator('section.lease');
  await expect(banner).toContainText('Inventory stopped');
  await expect(banner).toContainText('inventory-api');
  await expect(banner).toContainText('(smoke)');

  await banner.getByRole('button', { name: 'Restore now' }).click();
  // The banner follows the 3-second stack poll.
  await expect(banner).toHaveCount(0, { timeout: 10_000 });

  const state = await (await request.get('/api/stack/lease')).json();
  expect(state.lease).toBeNull();
});

test('a lease refuses Up with 409 naming the lease, and Up works again once it is restored', async ({ request }) => {
  expect(
    (await request.post('/api/stack/lease', { data: { changes: ['short-waits'], holdSeconds: 60 } })).status(),
  ).toBe(200);

  const refused = await request.post('/api/stack/backend/up');
  expect(refused.status()).toBe(409);
  expect((await refused.json()).detail).toContain('holds a lease');

  expect((await request.post('/api/stack/lease/restore')).status()).toBe(200);
  expect((await request.post('/api/stack/backend/up')).status()).toBe(202);
});
