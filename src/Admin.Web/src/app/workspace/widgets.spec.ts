import { SCREENS } from '../screens';
import { builtInTitle, builtInWidgets, defaultWorkspace, differsFromBuiltIn, withEveryBuiltIn } from './built-in-pages';
import { SCREEN_COMPONENTS } from './screen-components';
import { JOB_OUTPUT, LEGACY_SCREEN, NOTE, SCENARIO_WIDGETS, STACK_WIDGETS, WIDGETS, widgetType } from './widgets';
import { GRID_COLUMNS, MAX_ROWS, WidgetInstance } from './workspace-types';

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

  it('each start as one legacy widget across the grid, until a phase builds one from widgets', () => {
    for (const screen of SCREENS.filter((s) => s.path !== 'stack' && s.path !== 'scenario')) {
      expect(builtInWidgets(screen.path)).toEqual([
        expect.objectContaining({ type: 'legacy', x: 0, y: 0, cols: GRID_COLUMNS, config: { screen: screen.path } }),
      ]);
    }
  });

  it('build the Scenario page from the Scenario widget and the Saga strip beside it', () => {
    expect(builtInWidgets('scenario')).toEqual([
      expect.objectContaining({ id: 'builtin-scenario-scenario', type: 'scenario', x: 0, y: 0, cols: 16 }),
      expect.objectContaining({ id: 'builtin-scenario-saga-strip', type: 'saga-strip', x: 16, y: 0, cols: 8 }),
    ]);
    expect(Object.values(SCENARIO_WIDGETS).every((k) => k.presets.length === 1 && k.dataServices.includes('ScenarioRunner'))).toBe(true);
  });

  it('build the Stack page from the Stack widgets and a Job output following the page, none overlapping', () => {
    const widgets = builtInWidgets('stack');

    expect(widgets.map((w) => w.type).sort()).toEqual([...Object.values(STACK_WIDGETS).map((k) => k.type), JOB_OUTPUT.type].sort());
    expect(widgets.find((w) => w.type === JOB_OUTPUT.type)?.config).toEqual({ jobId: null });
    expect(new Set(widgets.map((w) => w.id)).size).toBe(widgets.length);
    for (const w of widgets) {
      const kind = widgetType(w.type)!;
      expect([w.cols, w.rows]).toEqual([kind.defaultSize.cols, kind.defaultSize.rows]);
      expect(w.x + w.cols).toBeLessThanOrEqual(GRID_COLUMNS);
    }
    const overlap = (a: WidgetInstance, b: WidgetInstance) =>
      a.x < b.x + b.cols && b.x < a.x + a.cols && a.y < b.y + b.rows && b.y < a.y + a.rows;
    for (const a of widgets) for (const b of widgets) if (a !== b) expect(overlap(a, b), `${a.id} / ${b.id}`).toBe(false);
  });
});

describe('the Stack widget types', () => {
  it('keep a list of names to show, and fall back to every one for anything else', () => {
    expect(STACK_WIDGETS.services.config({ services: ['sql', 'gateway'] })).toEqual({ services: ['sql', 'gateway'] });
    expect(STACK_WIDGETS.services.config({ services: 'sql' })).toEqual({ services: [] });
    expect(STACK_WIDGETS.reachability.config({ surfaces: [1] })).toEqual({ surfaces: [] });
    expect(STACK_WIDGETS.signals.titleOf({ services: ['Gateway.Api'] })).toBe('Golden signals (Gateway.Api)');
    expect(STACK_WIDGETS.services.titleOf({ services: [] })).toBe('Compose services');
  });

  it('show every alert rule, or the firing ones', () => {
    expect(STACK_WIDGETS.alerts.config({ firingOnly: 'yes' })).toEqual({ firingOnly: false });
    expect(STACK_WIDGETS.alerts.presets.map((p) => p.config)).toEqual([{ firingOnly: false }, { firingOnly: true }]);
    expect(STACK_WIDGETS.alerts.titleOf({ firingOnly: true })).toBe('Alerts (firing)');
  });

  it('drop config from a type that takes none', () => {
    expect(STACK_WIDGETS.controls.config({ confirm: 'down -v' })).toEqual({});
  });

  it('point a Job output at a job by id, or at its page', () => {
    expect(JOB_OUTPUT.config({ jobId: 'job-1' })).toEqual({ jobId: 'job-1' });
    expect(JOB_OUTPUT.config({ jobId: 7 })).toEqual({ jobId: null });
    expect(JOB_OUTPUT.titleOf({ jobId: null })).toBe('Job output');
  });

  it('title a Note by its first shown heading, not a # line inside a code fence', () => {
    expect(NOTE.titleOf({ source: '```bash\n# not a heading\n```\n\n## Real one\ntext' })).toBe('Real one');
    expect(NOTE.titleOf({ source: 'text only' })).toBe('Note');
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
