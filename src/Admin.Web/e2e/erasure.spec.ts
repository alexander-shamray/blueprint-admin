import { expect, test } from '@playwright/test';

// Against the fake platform's Privacy (src/Admin.Host/Fakes/FakeErasures.cs): a request reads open with three of the five
// holders answered, and closed on its next read; the subject ending 'dead' goes overdue, because Notifications and the BFF
// never answer until the request is reissued.
const OVERDUE_SUBJECT = '00000000-0000-0000-0000-00000000dead';

test('a raised request is read until every holder has answered', async ({ page }) => {
  await page.goto('/erasure');
  const holders = page.locator('table.holders tbody tr');

  await expect(holders).toHaveCount(5);
  await expect(holders.first()).toContainText('ordering [unasked] ordering-privacy 0');

  await page.getByRole('button', { name: 'New subject' }).click();
  await page.getByRole('button', { name: 'Raise' }).click();

  await expect(page.locator('.status')).toContainText('[open]');
  await expect(page.locator('table.holders tbody tr.holder-answered')).toHaveCount(3);
  await expect(page.locator('table.holders tbody tr.holder-waiting')).toHaveCount(2);
  await expect(page.getByRole('button', { name: 'Reissue' })).toHaveCount(0);

  await page.getByRole('button', { name: 'Read', exact: true }).click();

  await expect(page.locator('.status')).toContainText('[closed]');
  await expect(page.locator('table.holders tbody tr.holder-answered')).toHaveCount(5);
});

test('an overdue request is reissued and then closes', async ({ page }) => {
  await page.goto('/erasure');
  await page.getByLabel('Subject id').fill(OVERDUE_SUBJECT);
  await page.getByRole('button', { name: 'Raise' }).click();

  await expect(page.locator('.status')).toContainText('[overdue]');
  await expect(page.locator('table.holders tbody tr.holder-overdue')).toHaveCount(2);
  await expect(page.getByRole('link', { name: 'erasure-overdue runbook' })).toHaveAttribute('href', /\/docs\/runbooks\/erasure-overdue\.md$/);

  await page.getByRole('button', { name: 'Reissue' }).click();

  await expect(page.locator('.status')).toContainText('[closed]');
  await expect(page.locator('.facts')).toContainText('reissued 1 time');
  await expect(page.locator('table.holders tbody tr.holder-answered')).toHaveCount(5);
});
