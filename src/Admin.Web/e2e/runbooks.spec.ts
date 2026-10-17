import { expect, test } from '@playwright/test';

// The Runbooks screen against the fake platform (src/Admin.Host/Fakes/RunbookRecordings.cs): four entries of the game
// day's catalogue, three runbook texts, and a cause that fires its alert in the alert read until Undo takes it back.
test('a runbook is caused, watched firing beside its text, and undone', async ({ page }) => {
  await page.goto('/runbooks');
  await expect(page.getByRole('button', { name: /^stuck-saga/ })).toContainText('[no script]');

  await page.getByRole('button', { name: /^outbox-growth/ }).click();
  const detail = page.getByRole('region', { name: 'outbox-growth' });
  const text = page.getByRole('region', { name: 'Runbook text' });
  await expect(text.getByRole('heading', { level: 3 })).toContainText('Runbook');
  await expect(detail.locator('td.state')).toHaveText('[inactive]');

  // An outage takes the Cause button twice: the first press arms it and names what it costs.
  const cause = detail.getByRole('button', { name: /^Cause OutboxGrowth/ });
  await cause.click();
  await expect(detail.getByRole('button', { name: 'Confirm: cause OutboxGrowth (outage)' })).toBeVisible();
  await detail.getByRole('button', { name: 'Confirm: cause OutboxGrowth (outage)' }).click();

  const log = detail.getByRole('log', { name: 'Job output' });
  await expect(log).toContainText('game day: cause OK');
  await expect(log).toContainText('exited 0');
  await expect(detail.locator('td.state')).toHaveText('[firing]');

  await detail.getByRole('button', { name: 'Undo' }).click();
  await expect(log).toContainText('game day: restore OK');
  await expect(detail.locator('td.state')).toHaveText('[inactive]');
});

test('a cause that deletes data is not offered until the runbook name is typed', async ({ page }) => {
  await page.goto('/runbooks');
  await page.getByRole('button', { name: /^contact-refused/ }).click();
  const detail = page.getByRole('region', { name: 'contact-refused' });

  const cause = detail.getByRole('button', { name: 'Cause ContactReadRefused' });
  await expect(cause).toBeDisabled();
  await detail.getByLabel('Type contact-refused to confirm the cause').fill('contact-refused');
  await expect(cause).toBeEnabled();
  await cause.click();

  await expect(detail.getByRole('log', { name: 'Job output' })).toContainText('game day: cause OK');
  await expect(detail.locator('td.state')).toHaveText('[firing]');

  await detail.getByRole('button', { name: 'Undo' }).click();
  await expect(detail.locator('td.state')).toHaveText('[inactive]');
});
