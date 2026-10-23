import { expect, test } from '@playwright/test';
import { OFF_URL } from './hosts';
import { openPalette } from './palette';

// Provider=off on FakePlatform (AI assistant design §9.1): the console shows no trace of the assistant, and loses nothing.
test.use({ baseURL: OFF_URL });

test('with the assistant off there is no panel, no toggle and no palette entry', async ({ page }) => {
  await page.goto('/stack');
  await expect(page.getByRole('navigation', { name: 'Pages' })).toBeVisible();

  await expect(page.getByRole('button', { name: 'Assistant', exact: true })).toHaveCount(0);
  await expect(page.locator('app-assistant-panel')).toHaveCount(0);

  await openPalette(page);
  await page.keyboard.type('assistant');
  await expect(page.getByRole('option', { name: 'Ask the assistant' })).toHaveCount(0);
});
