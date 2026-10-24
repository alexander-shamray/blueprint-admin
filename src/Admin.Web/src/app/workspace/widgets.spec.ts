import { SCREENS } from '../screens';
import {
  WORKSPACE_VERSION,
  builtInTitle,
  builtInWidgets,
  defaultWorkspace,
  differsFromBuiltIn,
  retireLegacy,
  withEveryBuiltIn,
} from './built-in-pages';
import {
  API_WIDGETS,
  BROKER_WIDGETS,
  ERASURE,
  JOB_OUTPUT,
  LOGS,
  MAIL,
  MAP,
  NOTE,
  RUNBOOKS,
  SEQUENCE,
  TRACE,
  TRANSCRIPT,
  SCENARIO_WIDGETS,
  STACK_WIDGETS,
  WIDGETS,
  widgetType,
} from './widgets';
import { GRID_COLUMNS, MAX_ROWS, WidgetInstance, Workspace } from './workspace-types';

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

  // What the assistant explains each widget from (AI assistant design §6.1): two to five sentences, on every type.
  it.each(WIDGETS.map((w) => [w.type, w.help] as const))('%s has help of two to five sentences', (_, help) => {
    const sentences = help.split(/[.!?](?:\s|$)/).filter((s) => s.trim() !== '');
    expect(sentences.length).toBeGreaterThanOrEqual(2);
    expect(sentences.length).toBeLessThanOrEqual(5);
  });

  it('knows no type it does not list, the retired legacy screen widget among them', () => {
    expect(widgetType('legacy')).toBeUndefined();
    expect(widgetType('from-a-newer-console')).toBeUndefined();
  });
});

describe('retiring the legacy screen widget (phase 6)', () => {
  const legacy = (id: string, screen: unknown, x: number, y: number, cols: number, rows: number): WidgetInstance => ({
    id,
    type: 'legacy',
    x,
    y,
    cols,
    rows,
    config: { screen },
  });

  /** A phase-1 document as the host hands it on, stepped to version 2 with its widgets untouched. */
  function phase1(): Workspace {
    return {
      version: WORKSPACE_VERSION,
      activePageId: 'mine',
      pages: [
        { id: 'stack', title: 'Stack', builtIn: 'stack', hidden: false, widgets: null },
        {
          id: 'mine',
          title: 'Debugging',
          builtIn: null,
          hidden: false,
          widgets: [
            legacy('old-trace', 'trace', 0, 0, 24, 16),
            { id: 'below', type: 'note', x: 0, y: 16, cols: 6, rows: 4, config: { source: 'kept' } },
          ],
        },
        {
          id: 'mail',
          title: 'My mail',
          builtIn: 'mail',
          hidden: false,
          widgets: [legacy('old-mail', 'mail', 4, 2, 8, 6), legacy('old-other', 'not-a-screen', 0, 20, 24, 4)],
        },
      ],
    };
  }

  it("replaces each legacy instance with its screen's widgets inside its rectangle and names the pages it changed", () => {
    const { workspace, pages } = retireLegacy(phase1());

    expect(pages).toEqual(['Debugging', 'My mail']);
    const all = workspace.pages.flatMap((p) => p.widgets ?? []);
    expect(all.map((w) => w.type)).not.toContain('legacy');
    // An unchanged built-in page stays unsaved, so it keeps following the shipped defaults.
    expect(workspace.pages[0].widgets).toBeNull();

    const [trace, below] = workspace.pages[1].widgets!;
    expect(trace).toMatchObject({ type: TRACE.type, x: 0, y: 0, cols: TRACE.defaultSize.cols });
    expect(trace.config).toEqual(builtInWidgets('trace')[0].config);
    // What sat below the rectangle moves down by as far as the stack outgrew it, and is otherwise kept.
    expect(below).toEqual({
      id: 'below',
      type: 'note',
      x: 0,
      y: Math.max(16, TRACE.defaultSize.rows),
      cols: 6,
      rows: 4,
      config: { source: 'kept' },
    });
  });

  it('stacks a screen of several widgets in their default order, as wide as the rectangle down to each minimum', () => {
    const { workspace } = retireLegacy(phase1());
    const widgets = workspace.pages[2].widgets!;
    const [mail, ...stack] = widgets;

    const cols = Math.max(MAIL.minSize.cols, Math.min(MAIL.defaultSize.cols, 8));
    expect(mail).toMatchObject({ type: MAIL.type, x: Math.min(4, GRID_COLUMNS - cols), y: 2, cols });
    // A screen this console does not name was rendered as the first, Stack, so it becomes Stack's widgets, stacked,
    // starting where the rectangle did once the one above it has pushed it down.
    expect(stack.map((w) => w.type)).toEqual(builtInWidgets('stack').map((w) => w.type));
    expect(stack[0].y).toBe(20 + Math.max(0, 2 + MAIL.defaultSize.rows - 8));
    for (let i = 1; i < stack.length; i++) expect(stack[i].y).toBe(stack[i - 1].y + stack[i - 1].rows);
    expect(new Set(widgets.map((w) => w.id)).size).toBe(widgets.length);
  });

  it('puts a stack wider than its rectangle under the whole page rather than over a neighbour beside it', () => {
    // Runbooks' minimum is wider than this six-column rectangle, so in place it would reach the note to its right.
    expect(RUNBOOKS.minSize.cols).toBeGreaterThan(6);
    const beside: WidgetInstance = { id: 'beside', type: 'note', x: 6, y: 0, cols: 6, rows: 4, config: {} };
    const lower: WidgetInstance = { id: 'lower', type: 'note', x: 12, y: 10, cols: 6, rows: 3, config: {} };
    const workspace: Workspace = {
      version: WORKSPACE_VERSION,
      activePageId: 'p',
      pages: [
        {
          id: 'p',
          title: 'Narrow',
          builtIn: null,
          hidden: false,
          widgets: [legacy('old', 'runbooks', 0, 0, 6, 4), beside, lower],
        },
      ],
    };

    const widgets = retireLegacy(workspace).workspace.pages[0].widgets!;

    const [runbooks, ...kept] = widgets;
    expect(runbooks).toMatchObject({ type: RUNBOOKS.type, x: 0, y: 13, cols: RUNBOOKS.minSize.cols });
    expect(kept).toEqual([beside, lower]);
  });

  it('leaves no two widgets overlapping on any page it changed', () => {
    for (const page of retireLegacy(phase1()).workspace.pages) {
      const widgets = page.widgets ?? [];
      for (const a of widgets) {
        for (const b of widgets) {
          if (a === b) continue;
          const apart = a.x + a.cols <= b.x || b.x + b.cols <= a.x || a.y + a.rows <= b.y || b.y + b.rows <= a.y;
          expect(apart, `${page.title}: ${a.id} and ${b.id}`).toBe(true);
        }
      }
    }
  });

  it('leaves a workspace with no legacy instance as it is', () => {
    const workspace = defaultWorkspace();
    expect(retireLegacy(workspace)).toEqual({ workspace, pages: [] });
    expect(retireLegacy(workspace).workspace).toBe(workspace);
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

  it('are every one built from widgets of their own: no built-in page is a legacy screen widget any more', () => {
    for (const screen of SCREENS) {
      const widgets = builtInWidgets(screen.path);
      expect(widgets.length, screen.path).toBeGreaterThan(0);
      expect(
        widgets.map((w) => w.type),
        screen.path,
      ).not.toContain('legacy');
      // Every widget a page ships with is one the registry knows, with its components loadable.
      for (const w of widgets) expect(widgetType(w.type), `${screen.path}: ${w.type}`).toBeDefined();
    }
  });

  it('offer the request sequence as a registered widget that reads the Trace history and follow services, and sits on no built-in page', () => {
    expect(widgetType('sequence')).toBe(SEQUENCE);
    expect(SEQUENCE.config({ correlationId: 7, window: '9h', follow: 'yes' })).toEqual({
      correlationId: '',
      window: '15m',
      follow: false,
    });
    expect(SEQUENCE.titleOf({ correlationId: 'abc' })).toBe('Request sequence (abc)');
    expect(SEQUENCE.presets.map((p) => p.config['follow'])).toEqual([false, true]);
    expect(SEQUENCE.dataServices).toEqual(['TraceHistory', 'TraceFollow']);
    for (const screen of SCREENS)
      expect(
        builtInWidgets(screen.path).map((w) => w.type),
        screen.path,
      ).not.toContain('sequence');
  });

  it('build the Runbooks and Transcript pages from one widget each', () => {
    expect(builtInWidgets('runbooks')).toEqual([
      expect.objectContaining({
        id: 'builtin-runbooks-runbooks',
        type: 'runbooks',
        cols: 24,
        config: { runbook: null },
      }),
    ]);
    expect(builtInWidgets('transcript')).toEqual([
      expect.objectContaining({ id: 'builtin-transcript-transcript', type: 'transcript', config: {} }),
    ]);
    expect(RUNBOOKS.config({ runbook: 7 })).toEqual({ runbook: null });
    expect(RUNBOOKS.titleOf({ runbook: 'error-queue' })).toBe('Runbooks (error-queue)');
    expect(TRANSCRIPT.dataServices).toEqual(['TranscriptFeed']);
  });

  it('build the Broker page from its six widgets, each reading BrokerFeed and none polling until switched on', () => {
    const widgets = builtInWidgets('broker');

    expect(widgets.map((w) => w.type)).toEqual([
      'queues',
      'bff-projection',
      'dead-letters',
      'exchanges',
      'permissions',
      'bindings',
    ]);
    expect(Object.values(BROKER_WIDGETS).every((k) => k.dataServices.includes('BrokerFeed'))).toBe(true);
    for (const type of ['queues', 'exchanges', 'bindings', 'permissions']) {
      expect(widgetType(type)?.config({})).toMatchObject({ autoRefresh: false, filter: '' });
    }
    expect(widgetType('queues')?.config({ parkedOnly: true, autoRefresh: 'yes', filter: 7 })).toEqual({
      filter: '',
      parkedOnly: true,
      autoRefresh: false,
    });
  });

  it('title a Dead-letter workbench and a filtered listing by what they were set to', () => {
    expect(BROKER_WIDGETS.deadLetters.titleOf({ queue: 'a_error' })).toBe('Dead letters (a_error)');
    expect(BROKER_WIDGETS.deadLetters.titleOf({ queue: null })).toBe('Dead-letter workbench');
    expect(BROKER_WIDGETS.queues.titleOf({ filter: 'bff' })).toBe('Queues (bff)');
    expect(BROKER_WIDGETS.queues.titleOf({ filter: '' })).toBe('Queues');
  });

  it('build the Map, Mail and Erasure pages from one widget each, none polling until switched on', () => {
    expect(builtInWidgets('map')).toEqual([
      expect.objectContaining({ id: 'builtin-map-platform-map', type: 'platform-map', config: { autoRefresh: false } }),
    ]);
    expect(builtInWidgets('mail')).toEqual([
      expect.objectContaining({ id: 'builtin-mail-mail', type: 'mail', config: { correlationId: '' } }),
    ]);
    expect(builtInWidgets('erasure')).toEqual([
      expect.objectContaining({ id: 'builtin-erasure-erasure', type: 'erasure', config: {} }),
    ]);
    expect(MAP.config({ autoRefresh: 'yes' })).toEqual({ autoRefresh: false });
    expect(MAIL.config({ correlationId: 7 })).toEqual({ correlationId: '' });
    expect(MAIL.titleOf({ correlationId: 'abc' })).toBe('Mail (abc)');
    expect(MAIL.titleOf({ correlationId: '' })).toBe('Mail');
    expect([MAP.dataServices, ERASURE.dataServices]).toEqual([['MapFeed'], ['ErasureRun']]);
  });

  it('build the API page from a request widget beside a history widget, and the Trace page from one that follows', () => {
    expect(builtInWidgets('requests')).toEqual([
      expect.objectContaining({ id: 'builtin-requests-api-request', type: 'api-request', x: 0, config: {} }),
      expect.objectContaining({ id: 'builtin-requests-api-history', type: 'api-history', x: 16, config: {} }),
    ]);
    expect(builtInWidgets('trace')).toEqual([
      expect.objectContaining({
        id: 'builtin-trace-trace',
        type: 'trace',
        config: { correlationId: '', window: '15m', follow: true },
      }),
    ]);
    expect(API_WIDGETS.request.config({ operation: 7, identity: 'root' })).toEqual({ operation: null, identity: null });
    expect(API_WIDGETS.request.config({ operation: 'catalog:GetProducts', identity: 'custom:alice' })).toEqual({
      operation: 'catalog:GetProducts',
      identity: 'custom:alice',
    });
    expect(TRACE.config({ correlationId: 7, window: '9h', follow: 'yes' })).toEqual({
      correlationId: '',
      window: '15m',
      follow: false,
    });
    expect(TRACE.titleOf({ correlationId: 'abc' })).toBe('Trace (abc)');
    expect(TRACE.presets.map((p) => p.config['follow'])).toEqual([false, true]);
  });

  it('build the Logs page from one Logs widget over every service', () => {
    expect(builtInWidgets('logs')).toEqual([
      expect.objectContaining({
        id: 'builtin-logs-logs',
        type: 'logs',
        x: 0,
        y: 0,
        cols: 24,
        config: { services: [] },
      }),
    ]);
    expect(LOGS.dataServices).toEqual(['LogFollowService']);
  });

  it('build the Scenario page from the Scenario widget and the Saga strip beside it', () => {
    expect(builtInWidgets('scenario')).toEqual([
      expect.objectContaining({ id: 'builtin-scenario-scenario', type: 'scenario', x: 0, y: 0, cols: 16 }),
      expect.objectContaining({ id: 'builtin-scenario-saga-strip', type: 'saga-strip', x: 16, y: 0, cols: 8 }),
    ]);
    expect(
      Object.values(SCENARIO_WIDGETS).every((k) => k.presets.length === 1 && k.dataServices.includes('ScenarioRunner')),
    ).toBe(true);
  });

  it('build the Stack page from the Stack widgets and a Job output following the page, none overlapping', () => {
    const widgets = builtInWidgets('stack');

    expect(widgets.map((w) => w.type).sort()).toEqual(
      [...Object.values(STACK_WIDGETS).map((k) => k.type), JOB_OUTPUT.type].sort(),
    );
    expect(widgets.find((w) => w.type === JOB_OUTPUT.type)?.config).toEqual({ jobId: null });
    expect(new Set(widgets.map((w) => w.id)).size).toBe(widgets.length);
    for (const w of widgets) {
      const kind = widgetType(w.type)!;
      expect([w.cols, w.rows]).toEqual([kind.defaultSize.cols, kind.defaultSize.rows]);
      expect(w.x + w.cols).toBeLessThanOrEqual(GRID_COLUMNS);
    }
    const overlap = (a: WidgetInstance, b: WidgetInstance) =>
      a.x < b.x + b.cols && b.x < a.x + a.cols && a.y < b.y + b.rows && b.y < a.y + a.rows;
    for (const a of widgets)
      for (const b of widgets) if (a !== b) expect(overlap(a, b), `${a.id} / ${b.id}`).toBe(false);
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
    const restored = withEveryBuiltIn({ version: WORKSPACE_VERSION, pages: [own], activePageId: 'p' });
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
