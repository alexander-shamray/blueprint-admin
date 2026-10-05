import { expect, test } from '@playwright/test';

// The command palette, driven by the keyboard alone (#81). Against the fake platform: demo-trace-0001 is the
// correlation id FakeGrafana's Loki recording carries, and the Scenario runs as scenario.spec.ts describes.
test('the palette goes to a screen by keyboard alone', async ({ page }) => {
  await page.goto('/stack');

  await page.keyboard.press('Control+k');
  await expect(page.getByRole('dialog', { name: 'Command palette' })).toBeVisible();
  await page.keyboard.type('broker');
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/\/broker$/);
  await expect(page.getByRole('dialog', { name: 'Command palette' })).toBeHidden();
});

test('a correlation id pasted into the palette lands on its timeline', async ({ page }) => {
  await page.goto('/stack');

  await page.keyboard.press('Control+k');
  await page.keyboard.type('demo-trace-0001');
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/\/trace\/demo-trace-0001$/);
  await expect(page.locator('p.summary')).toContainText('for demo-trace-0001');
});

test('an id the platform would not adopt is refused by the trace endpoint, not let through by the palette', async ({ page }) => {
  await page.goto('/stack');

  await page.keyboard.press('Control+k');
  await page.keyboard.type('not an id');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('Enter');

  await expect(page.locator('p.error')).toContainText('A correlation id is 1-128 characters');
});

test('the palette runs the Scenario, which is asked for and never put in the URL', async ({ page }) => {
  await page.goto('/stack');

  await page.keyboard.press('Control+k');
  await page.keyboard.type('run the scenario');
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/\/scenario$/);
  await expect(page.locator('ol.steps li.step .state', { hasText: '[ok]' })).toHaveCount(7, { timeout: 20_000 });
});

test('Escape closes the palette and leaves the screen as it was', async ({ page }) => {
  await page.goto('/logs');

  await page.keyboard.press('Control+k');
  await page.keyboard.type('broker');
  await page.keyboard.press('Escape');

  await expect(page.getByRole('dialog', { name: 'Command palette' })).toBeHidden();
  await expect(page).toHaveURL(/\/logs$/);
});
