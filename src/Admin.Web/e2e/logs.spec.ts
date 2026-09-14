import { expect, test } from '@playwright/test';

test('following logs streams lines and highlights a correlation id', async ({ page }) => {
  await page.goto('/logs');

  await page.getByLabel('gateway').check();
  await page.getByRole('button', { name: 'Follow' }).click();

  await expect(page.locator('.log .line')).toHaveCount(12);
  await expect(page.locator('.live')).toBeVisible();

  await page.getByPlaceholder('correlation id').fill('fake-corr-0001');
  await expect(page.locator('.log .line.hit')).toHaveCount(2);

  await page.getByPlaceholder('filter text').fill('ordering-api');
  // R6: the fake log has four lines containing "ordering-api" — three from
  // the ordering-api service itself, plus one rabbitmq line naming it — not
  // two as an earlier draft of this spec assumed.
  await expect(page.locator('.log .line')).toHaveCount(4);

  await page.getByRole('button', { name: 'Stop' }).click();
  await expect(page.locator('.live')).toHaveCount(0);
});
