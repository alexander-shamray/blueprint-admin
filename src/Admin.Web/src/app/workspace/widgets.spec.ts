import { SCREENS } from '../screens';
import { builtInTitle, builtInWidgets, defaultWorkspace, differsFromBuiltIn, withEveryBuiltIn } from './built-in-pages';
import { SCREEN_COMPONENTS } from './screen-components';
import { LEGACY_SCREEN, WIDGETS, widgetType } from './widgets';
import { GRID_COLUMNS, MAX_ROWS } from './workspace-types';

describe('the widget registry', () => {
  it('gives every type a component, sizes that fit the grid and a preset', async () => {
    for (const kind of WIDGETS) {
      expect(await kind.component()).toBeTypeOf('function');
      expect(kind.minSize.cols).toBeLessThanOrEqual(kind.defaultSize.cols);
      expect(kind.minSize.rows).toBeLessThanOrEqual(kind.defaultSize.rows);
      expect(kind.defaultSize.cols).toBeLessThanOrEqual(GRID_COLUMNS);
      expect(kind.defaultSize.rows).toBeLessThanOrEqual(MAX_ROWS);
      expect(kind.presets.length).toBeGreaterThan(0);
    }
    expect(new Set(WIDGETS.map((w) => w.type)).size).toBe(WIDGETS.length);
  });

  it('knows no type it does not list', () => {
    expect(widgetType('legacy')).toBe(LEGACY_SCREEN);
    expect(widgetType('from-a-newer-console')).toBeUndefined();
  });

  it('offers every screen to the legacy widget, and loads each one', async () => {
    expect(LEGACY_SCREEN.presets.map((p) => p.config['screen'])).toEqual(SCREENS.map((s) => s.path));
    for (const screen of SCREENS) expect(await SCREEN_COMPONENTS[screen.path]()).toBeTypeOf('function');
  });

  it('falls back to its default for a screen it does not know', () => {
    expect(LEGACY_SCREEN.config({ screen: 'nope', extra: 1 })).toEqual({ screen: SCREENS[0].path });
    expect(LEGACY_SCREEN.config({ screen: 'mail' })).toEqual({ screen: 'mail' });
    expect(LEGACY_SCREEN.titleOf({ screen: 'requests' })).toBe('API');
  });
});

describe('the built-in pages', () => {
  it('are the screens, unchanged, in their order', () => {
    const workspace = defaultWorkspace();
    expect(workspace.pages.map((p) => [p.id, p.title, p.builtIn, p.widgets])).toEqual(
      SCREENS.map((s) => [s.path, s.label, s.path, null]),
    );
    expect(workspace.activePageId).toBe(SCREENS[0].path);
  });

  it('each start as one legacy widget across the grid', () => {
    for (const screen of SCREENS) {
      expect(builtInWidgets(screen.path)).toEqual([
        expect.objectContaining({ type: 'legacy', x: 0, y: 0, cols: GRID_COLUMNS, config: { screen: screen.path } }),
      ]);
    }
  });

  it('come back when a saved workspace lacks them, and an operator’s page stays', () => {
    const own = { id: 'p', title: 'Mine', builtIn: null, hidden: false, widgets: [] };
    const restored = withEveryBuiltIn({ version: 1, pages: [own], activePageId: 'p' });
    expect(restored.pages[0]).toBe(own);
    expect(restored.pages.slice(1).map((p) => p.builtIn)).toEqual(SCREENS.map((s) => s.path));
  });

  it('can be reset once their widgets or their title changed', () => {
    const page = defaultWorkspace().pages[0];
    expect(differsFromBuiltIn(page)).toBe(false);
    expect(differsFromBuiltIn({ ...page, title: 'Mine' })).toBe(true);
    expect(differsFromBuiltIn({ ...page, widgets: [] })).toBe(true);
    expect(builtInTitle('requests')).toBe('API');
  });
});
