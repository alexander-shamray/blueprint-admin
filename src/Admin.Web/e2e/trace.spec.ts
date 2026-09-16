import { expect, test } from '@playwright/test';

// Against the fake platform's recorded Grafana answers (src/Admin.Host/Fakes/fixtures/grafana-*.json):
// six Loki lines for demo-trace-0001 across two traces, of which Tempo holds one (three spans; the
// failed first attempt's trace answers 404, as the real Tempo does for a trace it does not have).
// Six lines plus three spans plus the terminal projection marker is ten rows.
//
// There is no [publish] row, and that is the point of the screen: the backend's outbox row carries no
// trace context, so the publish runs in a trace this correlation id cannot reach. The terminal row
// says so in words.
test('the trace screen renders a correlation id timeline that ends at the outbox', async ({ page }) => {
  await page.goto('/trace');

  await page.getByLabel('Correlation id').fill('demo-trace-0001');
  await page.getByRole('button', { name: 'Trace' }).click();

  await expect(page).toHaveURL(/\/trace\/demo-trace-0001$/);
  await expect(page.locator('table.timeline tbody tr')).toHaveCount(10);
  await expect(page.locator('table.timeline tbody tr td.kind', { hasText: '[http]' })).toHaveCount(2);
  await expect(page.locator('table.timeline tbody tr td.kind', { hasText: '[outbox]' })).toHaveCount(1);
  await expect(page.locator('table.timeline tbody tr.kind-Error')).toHaveCount(1);

  const last = page.locator('table.timeline tbody tr').last();
  await expect(last.locator('td.kind')).toHaveText('[queued]');
  await expect(last).toContainText('ordering-catalog-events');
  await expect(last).toContainText('the outbox carries no trace context');

  // The failed first attempt's trace 404s, and the screen says so rather than quietly showing fewer spans.
  await expect(page.locator('p.warning')).toContainText('1 of 2 traces could not be read from Tempo');

  await expect(page.locator('table.timeline a.explore').first()).toHaveAttribute(
    'href',
    /^http:\/\/localhost:3000\/explore\?/,
  );
});

test('a trace is shareable by URL and the window chosen is the one reported back', async ({ page }) => {
  // Arriving directly on the id, as the API screen's "Trace this call" does, loads without a submit.
  await page.goto('/trace/demo-trace-0001?');

  await expect(page.locator('p.summary')).toContainText('10 events for demo-trace-0001 over the last 15m');

  await page.getByLabel('Window').selectOption('6h');
  await page.getByRole('button', { name: 'Reload' }).click();

  await expect(page.locator('p.summary')).toContainText('over the last 6h');
});
