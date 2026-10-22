import { Locator, Page, expect, test } from '@playwright/test';
import { SCREENS } from '../src/app/screens';

// The widget workspace (#383, widget-workspace spec §11). Against the fake platform, whose workspace file lives in a
// temp directory, so a page made here never reaches a real one; the walk deletes its page at the end all the same,
// because every spec file shares one host.
for (const { path, label } of SCREENS) {
  test(`the built-in ${label} page renders its screen`, async ({ page }) => {
    await page.goto(`/${path}`);

    const widget = page.locator('gridster-item');
    await expect(widget).toHaveCount(1);
    await expect(widget.locator('.head .title')).toHaveText(label);
    await expect(widget.locator('.body > *').first()).toBeVisible();
    await expect(widget.locator('.body .loading')).toHaveCount(0);
    await expect(page.getByRole('navigation', { name: 'Pages' }).getByRole('link', { name: label })).toHaveClass(/active/);
  });
}

test('a page made, filled, resized and rearranged is found as it was left after a reload', async ({ page }) => {
  const title = `Walk ${Date.now()}`;
  await page.goto('/stack');

  await page.getByRole('button', { name: 'New page' }).click();
  await expect(page).toHaveURL(/\/page\/page-/);
  const name = page.getByRole('textbox', { name: 'Page title' });
  await name.fill(title);
  await name.press('Enter');

  for (const screen of ['Logs', 'Map', 'Mail']) {
    await page.getByRole('button', { name: 'Add widget' }).click();
    await page.getByRole('menuitem', { name: 'Screen' }).click();
    await page.getByRole('menuitem', { name: screen, exact: true }).click();
  }
  const titles = page.locator('gridster-item .head .title');
  await expect(titles).toHaveText(['Logs', 'Map', 'Mail']);

  const items = page.locator('gridster-item');
  const first = items.nth(0);
  const mail = items.nth(2);

  // Halve the first widget from its south-east corner, then halve Mail and drag it by its header beside the first.
  const before = (await first.boundingBox())!;
  const resized = await halve(page, first);
  expect(resized.width).toBeLessThan(before.width);
  expect(resized.height).toBeLessThan(before.height);
  await mail.scrollIntoViewIfNeeded();
  await halve(page, mail);
  const head = (await mail.locator('.head').boundingBox())!;
  await page.mouse.move(head.x + 40, head.y + head.height / 2);
  await page.mouse.down();
  await page.mouse.move(resized.x + resized.width + 60, resized.y + 15, { steps: 25 });
  await page.mouse.up();
  const moved = (await mail.boundingBox())!;
  expect(moved.x).toBeGreaterThan(resized.x + resized.width - 10);

  // The save is debounced; wait for the bar to stop saying it is unsaved or saving.
  await expect(page.locator('.save-state')).toHaveCount(0, { timeout: 10_000 });
  await page.reload();

  await expect(page.getByRole('navigation', { name: 'Pages' }).getByRole('link', { name: title })).toHaveClass(/active/);
  await expect(titles).toHaveText(['Logs', 'Map', 'Mail']);
  const reloaded = (await first.boundingBox())!;
  expect(Math.abs(reloaded.height - resized.height)).toBeLessThan(2);
  expect(reloaded.width).toBeLessThan(before.width);
  const mailReloaded = (await mail.boundingBox())!;
  expect(mailReloaded.x).toBeGreaterThan(reloaded.x + reloaded.width - 10);
  expect(Math.abs(mailReloaded.y - reloaded.y)).toBeLessThan(2);

  await page.getByRole('button', { name: 'Page actions' }).click();
  await page.getByRole('menuitem', { name: 'Delete' }).click();
  await expect(page.getByRole('navigation', { name: 'Pages' }).getByRole('link', { name: title })).toHaveCount(0);
  // The delete is saved like any change; leave the shared host without the page.
  await expect(page.locator('.save-state')).toHaveCount(0, { timeout: 10_000 });
});

/** Drags a widget's south-east corner up and left by half its size, and answers its new box. */
async function halve(page: Page, item: Locator) {
  const box = (await item.boundingBox())!;
  const corner = (await item.locator('.handle-se').boundingBox())!;
  await page.mouse.move(corner.x + corner.width / 2, corner.y + corner.height / 2);
  await page.mouse.down();
  await page.mouse.move(corner.x - box.width / 2, corner.y - box.height / 2, { steps: 10 });
  await page.mouse.up();
  return (await item.boundingBox())!;
}

test('the layout is locked until Edit layout', async ({ page }) => {
  await page.goto('/logs');

  await expect(page.locator('gridster-item .handle-se')).toHaveCount(0);
  await page.getByRole('button', { name: 'Edit layout' }).click();
  await expect(page.locator('gridster-item .handle-se')).toHaveCount(1);
  await page.getByRole('button', { name: 'Done' }).click();
  await expect(page.locator('gridster-item .handle-se')).toHaveCount(0);
});

test('the palette lists the pages', async ({ page }) => {
  await page.goto('/stack');

  await page.keyboard.press('Control+k');
  await page.keyboard.type('transcript');
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/\/transcript$/);
});
