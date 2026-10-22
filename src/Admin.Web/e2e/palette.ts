import { Page, expect } from '@playwright/test';

/**
 * Ctrl+K once the shell is there to hear it. The app bootstraps after its translations load (AI assistant design
 * §5.5), which can be after the page's load event, so a key pressed as soon as `goto` returns can reach a page with
 * no palette listening yet.
 */
export async function openPalette(page: Page): Promise<void> {
  await expect(page.getByRole('navigation', { name: 'Pages' })).toBeVisible();
  await page.keyboard.press('Control+k');
  await expect(page.getByRole('dialog', { name: 'Command palette' })).toBeVisible();
}
