import { expect, test } from '@playwright/test';

// The fake logs -f output (src/Admin.Host/Fakes/FakePlatformScripts.cs) by Compose
// service: gateway 3, catalog-api 4, ordering-api 3, web-bff 1, rabbitmq 1. The fake
// replays only the services a follow names, as docker compose logs does.
test('following logs streams the selected services and highlights a correlation id', async ({ page }) => {
  await page.goto('/logs');

  await page.getByLabel('gateway', { exact: true }).check();
  await page.getByLabel('catalog-api', { exact: true }).check();
  const followed = page.waitForResponse((r) => r.url().endsWith('/api/logs/follow') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Follow' }).click();
  const { id } = (await (await followed).json()) as { id: string };

  // gateway 3 + catalog-api 4.
  await expect(page.locator('.log .line')).toHaveCount(7);
  await expect(page.locator('.live')).toBeVisible();
  // Unselected services are absent; no gateway or catalog-api line names these three.
  await expect(page.locator('.log')).not.toContainText('ordering-api');
  await expect(page.locator('.log')).not.toContainText('web-bff');
  await expect(page.locator('.log')).not.toContainText('rabbitmq');

  // fake-corr-0001: the gateway's first proxying line and catalog-api's "Listed 20 products".
  await page.getByPlaceholder('correlation id').fill('fake-corr-0001');
  await expect(page.locator('.log .line.hit')).toHaveCount(2);

  // fake-corr-0002: the gateway's second proxying line and catalog-api's "Published" and
  // "Dispatched" lines; ordering-api's "Consumed" line carries it too but is not selected.
  await page.getByPlaceholder('filter text').fill('fake-corr-0002');
  await expect(page.locator('.log .line')).toHaveCount(3);

  await page.getByRole('button', { name: 'Stop' }).click();
  await expect(page.locator('.live')).toHaveCount(0);
  // Stop reaches the host: the fake follow is long-running, so only the stop endpoint can end it.
  await expect
    .poll(async () => ((await (await page.request.get(`/api/jobs/${id}`)).json()) as { summary: { state: string } }).summary.state)
    .toBe('Exited');
});

test('leaving the logs screen ends the host follow it started', async ({ page }) => {
  await page.goto('/logs');
  await page.getByLabel('gateway', { exact: true }).check();
  const followed = page.waitForResponse((r) => r.url().endsWith('/api/logs/follow') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Follow' }).click();
  const { id } = (await (await followed).json()) as { id: string };
  await expect(page.locator('.live')).toBeVisible();

  // The shell's link, not page.goto: a reload would end the page without running its teardown.
  await page.getByRole('link', { name: 'Stack' }).click();
  await expect(page).toHaveURL(/\/stack$/);

  await expect
    .poll(async () => ((await (await page.request.get(`/api/jobs/${id}`)).json()) as { summary: { state: string } }).summary.state)
    .toBe('Exited');
});
