import { Page, expect, test } from '@playwright/test';

// The fake logs -f output (src/Admin.Host/Fakes/FakePlatformScripts.cs) by Compose
// service: gateway 3, catalog-api 4, ordering-api 3, web-bff 1, rabbitmq 1. The fake
// replays only the services a follow names, as docker compose logs does.

/** Names the Logs widget's services in its settings panel, which the layout editor opens. */
async function showServices(page: Page, services: string): Promise<void> {
  await page.getByRole('button', { name: 'Edit layout' }).click();
  await page.getByRole('button', { name: 'Configure Logs' }).click();
  await page.getByRole('menuitem', { name: 'Edit settings…' }).click();
  const settings = page.getByRole('form', { name: 'Settings of Logs' });
  await settings.getByRole('textbox').fill(services);
  await settings.getByRole('button', { name: 'Apply' }).click();
  await page.getByRole('button', { name: 'Done' }).click();
}

async function jobState(page: Page, id: string): Promise<string> {
  return ((await (await page.request.get(`/api/jobs/${id}`)).json()) as { summary: { state: string } }).summary.state;
}

test('following logs streams the configured services and highlights a correlation id', async ({ page }) => {
  await page.goto('/logs');
  await showServices(page, 'gateway, catalog-api');

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
  await expect.poll(() => jobState(page, id)).toBe('Exited');
});

test('leaving the logs page leaves the follow running, and only Stop ends it', async ({ page }) => {
  await page.goto('/logs');
  await showServices(page, 'gateway');
  const followed = page.waitForResponse((r) => r.url().endsWith('/api/logs/follow') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Follow' }).click();
  const { id } = (await (await followed).json()) as { id: string };
  await expect(page.locator('.live')).toBeVisible();
  await expect(page.locator('.log .line')).toHaveCount(3);

  // The shell's link, not page.goto: a reload would end the page's memory of the job.
  await page.getByRole('link', { name: 'Stack' }).click();
  await expect(page).toHaveURL(/\/stack$/);
  // The shell says a follow is running, and links back to it.
  await expect(page.getByRole('link', { name: /Logs: following/ })).toBeVisible();
  expect(await jobState(page, id)).toBe('Running');

  await page.getByRole('link', { name: /Logs: following/ }).click();
  await expect(page).toHaveURL(/\/logs$/);
  await expect(page.locator('.live')).toBeVisible();
  // The lines held while the page was away are still there.
  await expect(page.locator('.log .line')).toHaveCount(3);

  await page.getByRole('button', { name: 'Stop' }).click();
  await expect.poll(() => jobState(page, id)).toBe('Exited');
});
