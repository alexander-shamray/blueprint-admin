import { expect, test } from '@playwright/test';

// FakePlatformScripts answers run-locally.md's stress run: its up puts the gateway and both workers on the override, which the
// fake ps then labels with docker-compose.stress.yml, and its run block prints a cut staircase a line at a time
// (StressRunRecordings), breaking at step 4 with k6's 99. The run claims ComposeGate, so each test ends on the Up that is the
// way back; one that fails before it sends the Stop and the Up after it, so the next file meets a normal stack.
test.describe('the stress run', () => {
  test.afterEach(async ({ request }, testInfo) => {
    if (testInfo.status !== testInfo.expectedStatus) {
      await request.post('/api/stack/stress-run/stop');
      await request.post('/api/stack/backend/up');
    }
  });

  test('a stress run shows its step and its knee, and the override stays said until the way back', async ({ page }) => {
    test.setTimeout(90_000);
    await page.goto('/stack');
    const stress = page.getByRole('region', { name: 'Stress run' });

    await expect(stress).toContainText('never what production can carry');
    await expect(stress.getByRole('button', { name: 'Stop stress' })).toBeDisabled();
    await stress.getByRole('button', { name: 'Run stress' }).click();

    const output = page.locator('app-output-pane pre');
    await expect(output).toContainText('Container commerce-gateway-1  Recreated');
    const banner = page.getByRole('status', { name: 'Stress override' });
    await expect(banner).toContainText(
      'gateway, shipping-worker, notifications-worker were created with the stress override',
    );

    // Each hold lasts long enough for the stack's 3-second poll to read it.
    await expect(stress.locator('.stress-run-step')).toHaveText(/Step [1-4]: holding at \d+ of 400 users/, {
      timeout: 40_000,
    });

    await expect(stress.locator('.stress-run-status')).toHaveText(/exited 99/, { timeout: 40_000 });
    await expect(stress.locator('.stress-run-knee')).toHaveText(/^Knee: step 4 \(200 VUs\): failures/);
    await expect(output).toContainText('| 4 | 200 | 52.6 | 4210 | 9870 | 23.4% (past the abort rate) |');
    await expect(output).toContainText('k6 exited 99: a step');

    await stress.getByRole('button', { name: 'Back to the normal profile' }).click();
    await expect(output).toContainText('Container commerce-gateway-1  Healthy');
    await expect(banner).toBeHidden();
    await expect(stress.getByRole('button', { name: 'Back to the normal profile' })).toBeHidden();
  });

  test('Stop ends a stress run with neither a pass nor a fail, and leaves the override for the way back', async ({
    page,
  }) => {
    test.setTimeout(60_000);
    await page.goto('/stack');
    const stress = page.getByRole('region', { name: 'Stress run' });

    await stress.getByRole('button', { name: 'Run stress' }).click();
    await expect(stress.locator('.stress-run-step')).toContainText('of 400 users', { timeout: 20_000 });
    await expect(stress.getByRole('button', { name: 'Back to the normal profile' })).toBeDisabled();
    await stress.getByRole('button', { name: 'Stop stress' }).click();

    const output = page.locator('app-output-pane pre');
    await expect(output).toContainText('neither a pass nor a fail');
    await expect(stress.locator('.stress-run-status')).toHaveText(/exited -1/);
    await expect(page.getByRole('status', { name: 'Stress override' })).toBeVisible();

    await stress.getByRole('button', { name: 'Back to the normal profile' }).click();
    await expect(page.getByRole('status', { name: 'Stress override' })).toBeHidden();
  });
});
