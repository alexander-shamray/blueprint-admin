import { expect, test } from '@playwright/test';

// The copy button writes to the clipboard, and the test reads it back from the page.
test.use({ permissions: ['clipboard-read', 'clipboard-write'] });

// Against the fake platform: the Scenario's four HTTP steps go through the proxy as demo, whose token is a JWT
// FakeKeycloak issues. The transcript keeps them as curl, and what the copy button hands over must hold none of it.
test('the transcript keeps what the operator did, and the copied script carries no secret', async ({ page }) => {
  await page.goto('/scenario');
  await page.getByRole('button', { name: 'Run' }).click();
  await expect(page.locator('ol.steps li.step .state', { hasText: '[ok]' })).toHaveCount(5);

  await page.goto('/transcript');
  const requests = page.locator('li.entry.request', { hasText: '/api/v1/orders' });
  await expect(requests.first()).toBeVisible();
  await expect(requests.first().locator('.identity')).toHaveText('as demo');
  await expect(requests.first().locator('.command')).toContainText("-H 'Authorization: Bearer <scrubbed>'");
  // The entries as the page shows them, which the host does not scrub again as a whole the way it does the script.
  await expect(page.locator('li.entry .command', { hasText: /eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/ })).toHaveCount(0);

  await page.getByRole('button', { name: 'Copy as shell script' }).click();
  await expect(page.getByRole('status')).toHaveText('Copied.');

  // The Windows clipboard hands LF text back as CRLF; the line endings are the OS's, not what is under test.
  const script = (await page.evaluate(() => navigator.clipboard.readText())).replace(/\r\n/g, '\n');
  expect(script.startsWith('#!/usr/bin/env bash\n')).toBe(true);
  expect(script).toContain('curl -i -X POST http://localhost:5000/api/v1/orders');
  expect(script).toContain('Authorization: Bearer <scrubbed>');
  expect(script).not.toMatch(/eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/);
});
