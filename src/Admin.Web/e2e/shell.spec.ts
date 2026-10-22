import { expect, test } from '@playwright/test';

// The shell around every screen (#381): a side navigation of the screens, and a top bar whose theme switch
// sets data-theme on <html> and survives a reload. Against the fake platform, which only supplies the config.
test('the side navigation reaches a screen and marks it', async ({ page }) => {
  await page.goto('/stack');

  const nav = page.getByRole('navigation', { name: 'Screens' });
  await nav.getByRole('link', { name: 'Broker' }).click();

  await expect(page).toHaveURL(/\/broker$/);
  await expect(nav.getByRole('link', { name: 'Broker' })).toHaveClass(/active/);
});

test('the theme switch applies light and dark and is remembered', async ({ page }) => {
  await page.goto('/stack');
  const html = page.locator('html');

  await page.getByRole('radio', { name: 'dark theme' }).click();
  await expect(html).toHaveAttribute('data-theme', 'dark');
  await expect(html).toHaveCSS('color-scheme', 'dark');

  await page.reload();
  await expect(html).toHaveAttribute('data-theme', 'dark');
  await expect(page.getByRole('radio', { name: 'dark theme' })).toBeChecked();

  await page.getByRole('radio', { name: 'system theme' }).click();
  await expect(html).not.toHaveAttribute('data-theme');
});
