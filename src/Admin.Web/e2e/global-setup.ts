import type { FullConfig } from '@playwright/test';

/**
 * Runs after the webServer is up and before any test. `reuseExistingServer` lets
 * Playwright attach to a host a developer already started, and the smoke clicks
 * Up and "Down and wipe": against a real host that is `docker compose down -v`
 * on the developer's volumes. So the run is refused unless the host on the
 * other end says it is in FakePlatform mode.
 */
export default async function globalSetup(config: FullConfig): Promise<void> {
  const baseURL = config.projects[0]?.use.baseURL ?? 'http://127.0.0.1:5300';
  const response = await fetch(`${baseURL}/api/config`);

  if (!response.ok) {
    throw new Error(`refusing to run the smoke: ${baseURL}/api/config answered ${response.status}`);
  }

  const { fakePlatform } = (await response.json()) as { fakePlatform?: unknown };

  if (fakePlatform !== true) {
    throw new Error(
      `refusing to run the smoke against a host that is not in FakePlatform mode; stop the host on ${new URL(baseURL).port}`,
    );
  }
}
