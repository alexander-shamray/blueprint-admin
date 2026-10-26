import { expect, test } from '@playwright/test';

// The console in several languages (AI assistant design §5.5, #427): one build, the chosen language's translations
// fetched from the host before bootstrap, and a picker that stores the choice and reloads. Against the fake platform,
// which only supplies the config and the Stack screen's typed confirmation.
test('the picker switches the shell to Russian through a reload, and back', async ({ page }) => {
  await page.goto('/stack');
  const html = page.locator('html');
  await expect(html).toHaveAttribute('lang', 'en-US');
  await expect(page.getByRole('navigation', { name: 'Pages' })).toBeVisible();

  // The picker's name holds the language it shows, so a voice-control user can say what they see.
  await expect(page.getByRole('button', { name: 'Language' })).toHaveAccessibleName('Language: English');
  await page.getByRole('button', { name: 'Language' }).click();
  await Promise.all([page.waitForEvent('load'), page.getByRole('menuitem', { name: 'Русский' }).click()]);

  await expect(html).toHaveAttribute('lang', 'ru-RU');
  await expect(page.getByRole('navigation', { name: 'Страницы' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Изменить раскладку' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Язык' })).toHaveAccessibleName('Язык: Русский');

  // The choice survives an ordinary reload.
  await page.reload();
  await expect(html).toHaveAttribute('lang', 'ru-RU');

  await page.getByRole('button', { name: 'Язык' }).click();
  await Promise.all([page.waitForEvent('load'), page.getByRole('menuitem', { name: 'English' }).click()]);
  await expect(html).toHaveAttribute('lang', 'en-US');
  await expect(page.getByRole('button', { name: 'Edit layout' })).toBeVisible();
});

test('a typed confirmation stays the literal text in Russian', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('admin.locale', 'ru-RU'));
  await page.goto('/stack');
  await expect(page.locator('html')).toHaveAttribute('lang', 'ru-RU');

  // The hint and the button are Russian now; the word the operator types is not.
  const confirm = page.getByPlaceholder('введите: down -v');
  await confirm.fill('down -v');
  await expect(page.getByRole('button', { name: 'Остановить и стереть' })).toBeEnabled();
});

test.describe('with a Russian browser and no stored choice', () => {
  test.use({ locale: 'ru-RU' });

  test('the console starts in Russian', async ({ page }) => {
    await page.goto('/stack');
    await expect(page.locator('html')).toHaveAttribute('lang', 'ru-RU');
    await expect(page.getByRole('navigation', { name: 'Страницы' })).toBeVisible();
  });
});
