import { expect, test } from '@playwright/test';

// Against the fake platform's mail sink (src/Admin.Host/Fakes/FakeMailSink.cs), answering from the four messages a live
// sink held for one order on 2026-10-15: placed, confirmed, despatched and delivered, each carrying that order's
// correlation id. The backend's mail is plain text, so the body opens as text.
const ORDER = '01a13f79-2194-77a5-bbad-eaf2d69eacd6';

test('one order, one screen, every message it caused, each linked to its trace', async ({ page }) => {
  await page.goto('/mail');
  const rows = page.locator('table.messages tbody tr');

  await expect(rows).toHaveCount(4);
  await expect(rows.first()).toContainText('Your order has been delivered');
  await expect(rows.last()).toContainText('We have received your order');
  await expect(page.locator('td.correlation a')).toHaveCount(4);
  await expect(page.getByRole('link', { name: "the sink's own UI" })).toHaveAttribute('href', 'http://localhost:8025');

  await page.getByLabel('Correlation id').fill(ORDER);
  await expect(rows).toHaveCount(4);
  await page.getByLabel('Correlation id').fill('another-order');
  await expect(rows).toHaveCount(0);
  await expect(page.locator('.count')).toContainText('0 of the 4 listed');
  await page.getByLabel('Correlation id').fill('');

  await rows.first().getByRole('button', { name: 'Open' }).click();
  await expect(page.locator('section.message h2')).toContainText('Your order has been delivered');
  await expect(page.locator('pre.body-text')).toContainText(`Order: ${ORDER}`);
  await expect(page.locator('iframe.body-html')).toHaveCount(0);

  await page
    .locator('section.message .facts')
    .getByRole('link', { name: `trace ${ORDER}` })
    .click();
  await expect(page).toHaveURL(new RegExp(`/trace/${ORDER}$`));
});

// §5.3: an old link keeps working. A trace's link to its order's mail lands on the built-in page with the filter set.
test('a link carrying a correlation id opens the mail already filtered to it', async ({ page }) => {
  await page.goto(`/mail?correlationId=${ORDER}`);
  await expect(page.getByLabel('Correlation id')).toHaveValue(ORDER);
  await expect(page.locator('table.messages tbody tr')).toHaveCount(4);

  await page.goto('/mail?correlationId=another-order');
  await expect(page.getByLabel('Correlation id')).toHaveValue('another-order');
  await expect(page.locator('table.messages tbody tr')).toHaveCount(0);
});

// §4.2: the message that is open and the filter typed are the widget's session state, so a page left and returned to finds them.
test('the message that is open and the filter typed survive leaving the page', async ({ page }) => {
  await page.goto('/mail');
  await page.getByLabel('Correlation id').fill(ORDER);
  await page.locator('table.messages tbody tr').first().getByRole('button', { name: 'Open' }).click();
  await expect(page.locator('section.message h2')).toContainText('Your order has been delivered');

  // The shell's link, not page.goto: a reload would empty everything and prove nothing.
  await page.getByRole('link', { name: 'Stack' }).click();
  await expect(page).toHaveURL(/\/stack$/);
  await page.getByRole('navigation', { name: 'Pages' }).getByRole('link', { name: 'Mail' }).click();
  await expect(page).toHaveURL(/\/mail$/);

  await expect(page.locator('section.message h2')).toContainText('Your order has been delivered');
  await expect(page.getByLabel('Correlation id')).toHaveValue(ORDER);
});
