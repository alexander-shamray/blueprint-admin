import { expect, test } from '@playwright/test';
import { OFF_URL } from './hosts';
import { openPalette } from './palette';

// Provider=off on FakePlatform (AI assistant design §9.1): the console shows no trace of the assistant, and loses nothing.
test.use({ baseURL: OFF_URL });

test('with the assistant off there is no panel, no toggle and no palette entry', async ({ page }) => {
  // The shell reads the status once, after it renders: the absences below mean something only once it has.
  const status = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/assistant');
  await page.goto('/stack');
  expect(await (await status).json()).toEqual({ enabled: false, provider: null, model: null, reason: 'off' });
  await expect(page.getByRole('navigation', { name: 'Pages' })).toBeVisible();

  await expect(page.getByRole('button', { name: 'Assistant', exact: true })).toHaveCount(0);
  await expect(page.locator('app-assistant-panel')).toHaveCount(0);

  await openPalette(page);
  await page.keyboard.type('assistant');
  await expect(page.getByRole('option', { name: 'Ask the assistant' })).toHaveCount(0);
});
