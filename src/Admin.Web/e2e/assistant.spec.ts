import { Page, expect, test } from '@playwright/test';
import { openPalette } from './palette';

// The assistant panel on FakePlatform (AI assistant design §14, "Playwright"): with no provider set, the fake one plays
// its scripts, which open a screen named in English or in Russian through the client tool open_screen, and answer a
// question about the stack from the host's read tool stack_status.

async function ask(page: Page, question: string): Promise<void> {
  await page.getByRole('textbox', { name: 'Message to the assistant' }).fill(question);
  await page.getByRole('button', { name: 'Send' }).click();
}

test('asked where the logs are, the assistant opens Logs', async ({ page }) => {
  await page.goto('/stack');
  await page.getByRole('button', { name: 'Assistant', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Assistant' })).toBeVisible();
  await expect(page.getByText('Answers from: fake')).toBeVisible();

  await ask(page, 'where are the logs?');

  await expect(page).toHaveURL(/\/logs$/);
  await expect(page.locator('app-assistant-panel .reply').last()).toHaveText('Logs is open.');
  await expect(page.locator('app-assistant-panel .tool')).toContainText('Opening Logs');
});

test('asked whether the stack is running, it answers from the stack read and says it read it', async ({ page }) => {
  await page.goto('/logs');
  await page.getByRole('button', { name: 'Assistant', exact: true }).click();

  await ask(page, 'is the stack running?');

  await expect(page.locator('app-assistant-panel .tool')).toContainText('Reading the stack');
  await expect(page.locator('app-assistant-panel .reply').last()).toHaveText(
    /^The backend has \d+ services, \d+ of them running\.$/,
  );
  // A read takes the operator nowhere.
  await expect(page).toHaveURL(/\/logs$/);
});

test('asked in Russian, it opens Logs and names it as labelled', async ({ page }) => {
  await page.goto('/stack');
  await page.getByRole('button', { name: 'Assistant', exact: true }).click();

  await ask(page, 'где логи?');

  await expect(page).toHaveURL(/\/logs$/);
  await expect(page.locator('app-assistant-panel .reply').last()).toHaveText('Экран «Logs» открыт.');
});

test('with the console in Russian the panel is in Russian, and a reload keeps the conversation', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('admin.locale', 'ru-RU'));
  await page.goto('/stack');
  await page.getByRole('button', { name: 'Ассистент', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Ассистент' })).toBeVisible();

  await page.getByRole('textbox', { name: 'Сообщение ассистенту' }).fill('где логи?');
  await page.getByRole('button', { name: 'Отправить' }).click();
  await expect(page).toHaveURL(/\/logs$/);
  await expect(page.locator('app-assistant-panel .reply').last()).toHaveText('Экран «Logs» открыт.');

  // The tab keeps its conversation's id, so the reloaded panel redraws the same conversation from the host.
  await page.reload();
  await page.getByRole('button', { name: 'Ассистент', exact: true }).click();
  await expect(page.locator('app-assistant-panel .operator')).toHaveText('где логи?');
  await expect(page.locator('app-assistant-panel .reply').last()).toHaveText('Экран «Logs» открыт.');
});

test('the palette opens the assistant', async ({ page }) => {
  await page.goto('/stack');

  await openPalette(page);
  await page.keyboard.type('assistant');
  await page.keyboard.press('Enter');

  await expect(page.getByRole('heading', { name: 'Assistant' })).toBeVisible();
});
