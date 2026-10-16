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

  await page.locator('section.message .facts').getByRole('link', { name: `trace ${ORDER}` }).click();
  await expect(page).toHaveURL(new RegExp(`/trace/${ORDER}$`));
});
