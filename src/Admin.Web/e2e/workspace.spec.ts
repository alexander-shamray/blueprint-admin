import { readFile } from 'node:fs/promises';
import { Locator, Page, expect, test } from '@playwright/test';
import { openPalette } from './palette';
import { SCREENS } from '../src/app/screens';

// The widget workspace (#383, widget-workspace spec §11). Against the fake platform, whose workspace file lives in a
// temp directory, so a page made here never reaches a real one; the walk deletes its page at the end all the same,
// because every spec file shares one host.
// The Stack (#385) and Scenario (#389) pages are built from their widgets; every other built-in page is still its
// screen in one widget.
for (const { path, label } of SCREENS.filter((s) => s.path !== 'stack' && s.path !== 'scenario')) {
  test(`the built-in ${label} page renders its screen`, async ({ page }) => {
    await page.goto(`/${path}`);

    const widget = page.locator('gridster-item');
    await expect(widget).toHaveCount(1);
    // The frame's own ws- classes: the screens inside have .head, .title and .loading of their own.
    await expect(widget.locator('.ws-head .ws-title')).toHaveText(label);
    await expect(widget.locator('.ws-body > *').first()).toBeVisible();
    await expect(widget.locator('.ws-body > app-legacy-screen-widget > .loading')).toHaveCount(0);
    await expect(page.getByRole('navigation', { name: 'Pages' }).getByRole('link', { name: label })).toHaveClass(/active/);
  });
}

test('the built-in Scenario page renders the Scenario widget beside the Saga strip', async ({ page }) => {
  await page.goto('/scenario');

  await expect(page.locator('gridster-item .ws-head .ws-title')).toHaveText(['Scenario', 'Saga strip']);
  await expect(page.locator('gridster-item .ws-body > *')).toHaveCount(2);
  await expect(page.getByLabel('Script')).toHaveValue('deliver');
  await expect(page.locator('.saga-none')).toBeVisible();
});

test('the built-in Stack page renders its widgets, each with what it reads', async ({ page }) => {
  await page.goto('/stack');

  await expect(page.locator('gridster-item .ws-head .ws-title')).toHaveText([
    'Workstation doctor',
    'Compose services',
    'Stack controls',
    'Alerts',
    'Job output',
    'Reachability',
    'Golden signals',
    'Load run',
    'Reference client',
  ]);
  // A frame renders before its lazy component loads, so wait for every body to hold one before reading its state.
  await expect(page.locator('gridster-item .ws-body > *')).toHaveCount(9);
  await expect(page.locator('gridster-item .loading')).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Workstation' }).locator('table.doctor tbody tr').first()).toBeVisible();
  await expect(page.locator('table.services tbody tr').first()).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Pages' }).getByRole('link', { name: 'Stack' })).toHaveClass(/active/);
});

test('a page made, filled, resized and rearranged is found as it was left after a reload', async ({ page }) => {
  // Tall enough for all three default-size widgets, so every corner and header the walk drags is on screen.
  await page.setViewportSize({ width: 1280, height: 2600 });
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
  const titles = page.locator('gridster-item .ws-head .ws-title');
  await expect(titles).toHaveText(['Logs', 'Map', 'Mail']);
  // `page.mouse` skips the actionability checks, so a closing menu's backdrop would take the first press.
  await expect(page.locator('.cdk-overlay-backdrop')).toHaveCount(0);

  const items = page.locator('gridster-item');
  const first = items.nth(0);
  const mail = items.nth(2);

  // Halve the first widget from its south-east corner, then halve Mail and drag it by its header beside the first.
  const before = (await first.boundingBox())!;
  const resized = await halve(page, first);
  expect(resized.width).toBeLessThan(before.width);
  expect(resized.height).toBeLessThan(before.height);
  await halve(page, mail);
  const head = (await mail.locator('.ws-head').boundingBox())!;
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

test('a widget type with one preset is added in one press, and one with several opens its presets', async ({ page }) => {
  await page.goto('/stack');
  await page.getByRole('button', { name: 'New page' }).click();
  await expect(page).toHaveURL(/\/page\/page-/);
  const made = page.url();

  await page.getByRole('button', { name: 'Add widget' }).click();
  await page.getByRole('menuitem', { name: 'Reachability' }).click();
  await page.getByRole('button', { name: 'Add widget' }).click();
  await page.getByRole('menuitem', { name: 'Alerts' }).click();
  await page.getByRole('menuitem', { name: 'Firing only' }).click();

  await expect(page.locator('gridster-item .ws-head .ws-title')).toHaveText(['Reachability', 'Alerts (firing)']);
  await expect(page.locator('gridster-item .reachability span.up').first()).toBeVisible();
  await expect(page.locator('.cdk-overlay-backdrop')).toHaveCount(0);

  await page.getByRole('button', { name: 'Page actions' }).click();
  await page.getByRole('menuitem', { name: 'Delete' }).click();
  // The open page moves on once the delete lands; only then is waiting for the save to settle a wait for this one.
  await expect(page).not.toHaveURL(made);
  // The delete is saved like any change; leave the shared host without the page.
  await expect(page.locator('.save-state')).toHaveCount(0, { timeout: 10_000 });
});

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

  await openPalette(page);
  await page.keyboard.type('transcript');
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/\/transcript$/);
});

test('a widget is configured by a preset and maximised to the page, then restored', async ({ page }) => {
  await page.goto('/stack');
  await page.getByRole('button', { name: 'New page' }).click();
  await expect(page).toHaveURL(/\/page\/page-/);
  const made = page.url();

  await page.getByRole('button', { name: 'Add widget' }).click();
  await page.getByRole('menuitem', { name: 'Alerts' }).click();
  await page.getByRole('menuitem', { name: 'Every rule' }).click();
  await page.getByRole('button', { name: 'Add widget' }).click();
  await page.getByRole('menuitem', { name: 'Reachability' }).click();
  await expect(page.locator('.cdk-overlay-backdrop')).toHaveCount(0);

  const titles = page.locator('gridster-item .ws-head .ws-title');
  await expect(titles).toHaveText(['Alerts', 'Reachability']);
  // A type with a choice to make, or a setting to write, has a configure control, and only while editing.
  await expect(page.getByRole('button', { name: 'Configure Alerts' })).toBeVisible();
  await page.getByRole('button', { name: 'Configure Alerts' }).click();
  await page.getByRole('menuitem', { name: 'Firing only' }).click();
  await expect(titles).toHaveText(['Alerts (firing)', 'Reachability']);

  // A setting no preset holds is written in the settings panel the menu opens.
  await page.getByRole('button', { name: 'Configure Reachability' }).click();
  await page.getByRole('menuitem', { name: 'Edit settings…' }).click();
  const settings = page.getByRole('form', { name: 'Settings of Reachability' });
  // Focus moves into the panel when it opens and back to the configure button when it closes.
  await expect(settings.getByRole('textbox')).toBeFocused();
  await settings.getByRole('textbox').fill('gateway, grafana');
  await settings.getByRole('button', { name: 'Apply' }).click();
  await expect(settings).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Configure Reachability (gateway, grafana)' })).toBeFocused();
  await expect(titles).toHaveText(['Alerts (firing)', 'Reachability (gateway, grafana)']);

  await page.getByRole('button', { name: 'Maximise Alerts (firing)' }).click();
  await expect(page.locator('gridster-item')).toHaveCount(0);
  await expect(page.locator('.ws-maximised .ws-title')).toHaveText('Alerts (firing)');
  await page.getByRole('button', { name: 'Restore Alerts (firing) to the page' }).click();
  await expect(titles).toHaveText(['Alerts (firing)', 'Reachability (gateway, grafana)']);

  await page.getByRole('button', { name: 'Page actions' }).click();
  await page.getByRole('menuitem', { name: 'Delete' }).click();
  await expect(page).not.toHaveURL(made);
  await expect(page.locator('.save-state')).toHaveCount(0, { timeout: 10_000 });
});

test('a note is added from a preset, draws its diagram, and is written in place while editing', async ({ page }) => {
  await page.goto('/stack');
  await page.getByRole('button', { name: 'New page' }).click();
  await expect(page).toHaveURL(/\/page\/page-/);
  const made = page.url();

  await page.getByRole('button', { name: 'Add widget' }).click();
  await page.getByRole('menuitem', { name: 'Note' }).click();
  await page.getByRole('menuitem', { name: 'Architecture diagram' }).click();
  await expect(page.locator('.cdk-overlay-backdrop')).toHaveCount(0);

  // While editing, the note is its source; the title is its first heading.
  await expect(page.locator('gridster-item .ws-head .ws-title')).toHaveText('Architecture');
  await expect(page.getByRole('textbox', { name: 'Note text' })).toHaveValue(/flowchart LR/);

  // The preset's drawing survives the sanitiser whole: its text, its edges and their arrowheads.
  await page.getByRole('button', { name: 'Done' }).click();
  const architecture = page.locator('gridster-item app-mermaid-diagram .drawing svg');
  await expect(architecture).toContainText('Admin.Host');
  expect(await architecture.locator('path').count()).toBeGreaterThan(10);
  expect(await architecture.locator('marker').count()).toBeGreaterThan(0);
  await page.getByRole('button', { name: 'Edit layout' }).click();

  await page.getByRole('textbox', { name: 'Note text' }).fill('# Mine\n\nHello <script>x</script>\n\n```mermaid\nflowchart LR\n  a["one"] --> b["two"]\n```');
  await page.getByRole('button', { name: 'Done' }).click();

  // Done: the text is rendered as text, and Mermaid, loaded now, has drawn the fence.
  await expect(page.locator('gridster-item .ws-head .ws-title')).toHaveText('Mine');
  await expect(page.locator('gridster-item .ws-body p')).toHaveText('Hello <script>x</script>');
  const drawing = page.locator('gridster-item app-mermaid-diagram .drawing svg');
  await expect(drawing).toBeVisible();
  await expect(drawing).toContainText('one');
  await expect(drawing).toContainText('two');
  await expect(page.locator('gridster-item script')).toHaveCount(0);

  await page.getByRole('button', { name: 'Page actions' }).click();
  await page.getByRole('menuitem', { name: 'Delete' }).click();
  await expect(page).not.toHaveURL(made);
  await expect(page.locator('.save-state')).toHaveCount(0, { timeout: 10_000 });
});

test('the workspace is exported as a file, and an import replaces it only after the operator says so', async ({ page }) => {
  await page.goto('/stack');
  await expect(page.locator('.save-state')).toHaveCount(0, { timeout: 10_000 });

  await page.getByRole('button', { name: 'Page actions' }).click();
  const download = page.waitForEvent('download');
  await page.getByRole('menuitem', { name: 'Export workspace' }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe('blueprint-admin-workspace.json');
  const text = await readFile((await file.path())!, 'utf8');
  expect(JSON.parse(text).pages.length).toBeGreaterThan(0);

  const choose = (name: string, content: string) =>
    page.locator('input[type=file]').setInputFiles({ name, mimeType: 'application/json', buffer: Buffer.from(content) });

  // Not JSON: refused in the browser, with nothing sent.
  await choose('broken.json', '{');
  await expect(page.getByText('"broken.json" is not a workspace file: it is not JSON.')).toBeVisible();

  // JSON that is not a workspace: asked first, then the host's own validation answers.
  await choose('nope.json', '{"nope":true}');
  await expect(page.getByRole('alert')).toContainText('"nope.json"');
  await page.getByRole('button', { name: 'Replace', exact: true }).click();
  await expect(page.getByText(/"nope.json" was not imported/)).toBeVisible();

  // The export, imported back, is the workspace it was; cancelling the question replaces nothing.
  await choose('workspace.json', text);
  await expect(page.getByRole('alert')).toContainText('"workspace.json"');
  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByRole('alert')).toHaveCount(0);
  await choose('workspace.json', text);
  await page.getByRole('button', { name: 'Replace', exact: true }).click();
  await expect(page.getByText('Workspace replaced by "workspace.json".')).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Pages' }).getByRole('link', { name: 'Stack' })).toBeVisible();
});
