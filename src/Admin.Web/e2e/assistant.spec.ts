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

// The ids FakePlatform's gateway holds: a stocked product and the order whose payment it recorded.
const STOCKED_PRODUCT = '0199a1b2-0000-7000-8000-00000000000a';
const PLACED_ORDER = '0199a1b2-0000-7000-8000-000000000002';

test("asked a product's stock and an order's payment, it answers from the platform reads and the transcript shows its curls", async ({
  page,
}) => {
  await page.goto('/stack');
  await page.getByRole('button', { name: 'Assistant', exact: true }).click();

  await ask(page, `how many are in stock for product ${STOCKED_PRODUCT}?`);
  await expect(page.locator('app-assistant-panel .reply').last()).toHaveText('10 in stock, 0 reserved.');
  await expect(page.locator('app-assistant-panel .tool').last()).toContainText('get_stock');

  await ask(page, `what is the payment status of order ${PLACED_ORDER}?`);
  await expect(page.locator('app-assistant-panel .reply').last()).toHaveText('The payment is Authorised.');
  await expect(page.locator('app-assistant-panel .tool').last()).toContainText('get_payment');

  // The assistant's reads are the transcript's, marked as such, and repeat as the curl the operator would type.
  await page.goto('/transcript');
  const asked = page.locator('li.entry', { hasText: 'asked by the assistant' });
  await expect(asked.filter({ hasText: `/api/v1/inventory/stock/${STOCKED_PRODUCT}` }).first()).toBeVisible();
  await expect(asked.filter({ hasText: `/api/v1/payments/${PLACED_ORDER}` }).first()).toBeVisible();
});

test("a realm user pinned in the panel is who the platform reads are made as, and the platform's refusal is the answer", async ({
  page,
}) => {
  await page.goto('/stack');
  await page.getByRole('button', { name: 'Assistant', exact: true }).click();

  // `privacy` holds no inventory permission, so the gateway refuses the stock read as it would the API screen's.
  await page.getByRole('combobox', { name: /Read the platform as/ }).selectOption('privacy');
  await ask(page, `how many are in stock for product ${STOCKED_PRODUCT}?`);
  await expect(page.locator('app-assistant-panel .reply').last()).toHaveText('The platform answered 403.');

  await page.goto('/transcript');
  const refused = page.locator('li.entry', { hasText: 'asked by the assistant' }).filter({ hasText: 'as privacy' });
  await expect(refused.first()).toContainText('HTTP 403');
});

test('asked where a widget lives, it opens the page that holds it and outlines the widget', async ({ page }) => {
  await page.goto('/stack');
  await page.getByRole('button', { name: 'Assistant', exact: true }).click();

  await ask(page, 'where is the Queues widget?');

  await expect(page).toHaveURL(/\/broker$/);
  await expect(page.locator('[data-widget-id="builtin-broker-queues"]')).toHaveClass(/assistant-highlight/);
  await expect(page.locator('app-assistant-panel .reply').last()).toHaveText(
    'The Queues widget is on the Broker page; I outlined it.',
  );
  await expect(page.locator('app-assistant-panel .tool').first()).toContainText('Opening Broker');
  // The outline is for a few seconds, not for good.
  await expect(page.locator('[data-widget-id="builtin-broker-queues"]')).not.toHaveClass(/assistant-highlight/);
});

test("a widget the assistant proposes is added by the operator's click and by nothing else", async ({ page }) => {
  await page.goto('/stack');
  await page.getByRole('button', { name: 'Assistant', exact: true }).click();

  await ask(page, 'please add a Note widget');

  await expect(page.locator('app-assistant-panel .reply').last()).toHaveText(
    'Press the button under this reply to add the Note widget.',
  );
  const add = page.getByRole('button', { name: 'Add Note' });
  await expect(add).toBeVisible();
  // Offered, not added: no Note on the page yet.
  await expect(page.getByRole('article', { name: 'Note', exact: true })).toHaveCount(0);

  await add.click();

  await expect(page.getByRole('article', { name: 'Note', exact: true })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Added Note' })).toBeDisabled();
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
