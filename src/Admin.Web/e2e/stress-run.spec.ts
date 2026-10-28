import { expect, test } from '@playwright/test';

// FakePlatformScripts answers run-locally.md's stress run: its up puts the gateway, both workers and Ordering on the override,
// which the fake ps then labels with docker-compose.stress.yml, and its run block prints a cut staircase a line at a time
// (StressRunRecordings), at the level and under the stop rule the lists chose, ending as that rule says: the default, low at the
// first knee, breaks at step 2 with k6's 108. The run claims ComposeGate, so each test ends on the Up that is the
// way back; one that fails before it sends the Stop and the Up after it, so the next file meets a normal stack.
test.describe('the stress run', () => {
  test.afterEach(async ({ request }, testInfo) => {
    if (testInfo.status !== testInfo.expectedStatus) {
      await request.post('/api/stack/stress-run/stop');
      await request.post('/api/stack/backend/up');
    }
  });

  test('a stress run shows its step, its knee, why it ended and who gave up first, and the override stays said until the way back', async ({
    page,
  }) => {
    test.setTimeout(90_000);
    await page.goto('/stack');
    const stress = page.getByRole('region', { name: 'Stress run' });

    await expect(stress).toContainText('never what production can carry');
    await expect(stress.getByRole('button', { name: 'Stop stress' })).toBeDisabled();
    await stress.getByRole('button', { name: 'Run stress' }).click();

    const output = page.locator('app-output-pane pre');
    const containers = page.getByRole('region', { name: 'Containers' });
    await expect(containers.getByRole('row', { name: /^commerce-gateway-1 healthy/ })).toBeVisible();
    await expect(page.locator('app-output-pane .steps li').first()).toContainText('k6');
    const banner = page.getByRole('status', { name: 'Stress override' });
    await expect(banner).toContainText(
      'gateway, shipping-worker, notifications-worker, ordering-api were created with the stress override',
    );

    // Each hold lasts long enough for the stack's 3-second poll to read it.
    await expect(stress.locator('.stress-run-step')).toHaveText(/Step [1-2]: holding at \d+ users/, {
      timeout: 40_000,
    });

    // The Job output's k6 bar reads the same hold, as the staircase of the steps the host has counted.
    const k6 = page.getByRole('region', { name: 'k6 run', exact: true });
    await expect(k6.locator('.pull-summary')).toHaveText(/^k6 · [\d:]+ of \d+:\d+ · \d+ of 12800 users/, {
      timeout: 40_000,
    });
    await expect(k6.locator('.staircase [aria-current="step"]')).toHaveText(/^\s*(Ramp|Hold) [1-4]\s*$/, {
      timeout: 40_000,
    });

    await expect(stress.locator('.stress-run-status')).toHaveText(/exited 108/, { timeout: 40_000 });
    await expect(stress.locator('.stress-run-knee')).toHaveText(/^Knee: step 2 \(200 VUs\): failures/);
    await expect(stress.locator('.stress-run-first')).toHaveText(/^First to give up: Ordering\.Api: 5xx 3\.2%/);
    await expect(stress.locator('.stress-run-ended')).toHaveText(
      'Ended: knee at step 2 (200 VUs); level low, stop rule knee',
    );
    await expect(output).toContainText('| 2 | 200 | 50.0 | 192 | 364 | 23.4% (past the abort rate) |');
    await expect(output).toContainText('k6 exited 108: the knee stop');

    await stress.getByRole('button', { name: 'Back to the normal profile' }).click();
    await expect(containers.getByRole('row', { name: /^commerce-gateway-1 healthy/ })).toBeVisible();
    await expect(banner).toBeHidden();
    await expect(stress.getByRole('button', { name: 'Back to the normal profile' })).toBeHidden();
  });

  test('the lists choose the level and the stop rule, the choice is remembered, and the run ends as the rule says', async ({
    page,
  }) => {
    test.setTimeout(120_000);
    await page.goto('/stack');
    const stress = page.getByRole('region', { name: 'Stress run' });
    const containers = page.getByRole('region', { name: 'Containers' });
    const back = stress.getByRole('button', { name: 'Back to the normal profile' });

    await stress.getByLabel('Level').selectOption('medium');
    await stress.getByLabel('Stop').selectOption('errors');
    await stress.getByRole('button', { name: 'Run stress' }).click();
    await expect(stress.getByLabel('Level')).toBeDisabled();
    await expect(stress.locator('.stress-run-status')).toHaveText(/exited 99/, { timeout: 60_000 });
    await expect(stress.locator('.stress-run-ended')).toHaveText(
      'Ended: errors at step 3 (3200 VUs); level medium, stop rule errors',
    );
    await back.click();
    await expect(containers.getByRole('row', { name: /^commerce-gateway-1 healthy/ })).toBeVisible();
    await expect(back).toBeHidden();

    // This browser remembers the lists; xhigh is the open level, which the generator ends before its first step is measured.
    await page.reload();
    await expect(stress.getByLabel('Level')).toHaveValue('medium');
    await expect(stress.getByLabel('Stop')).toHaveValue('errors');
    await stress.getByLabel('Level').selectOption('xhigh');
    await stress.getByRole('button', { name: 'Run stress' }).click();
    await expect(stress.locator('.stress-run-status')).toHaveText(/exited 0/, { timeout: 40_000 });
    await expect(stress.locator('.stress-run-first')).toHaveText('First to give up: none named');
    await expect(stress.locator('.stress-run-ended')).toHaveText(
      /^Ended: generator at step 1 \(12800 VUs\): k6 ran 10240 of 12800 VUs; level xhigh, stop rule errors$/,
    );
    await back.click();
    await expect(back).toBeHidden();
  });

  test('Stop ends a stress run with neither a pass nor a fail, and leaves the override for the way back', async ({
    page,
  }) => {
    test.setTimeout(60_000);
    await page.goto('/stack');
    const stress = page.getByRole('region', { name: 'Stress run' });

    await stress.getByRole('button', { name: 'Run stress' }).click();
    await expect(stress.locator('.stress-run-step')).toContainText(' users', { timeout: 20_000 });
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
