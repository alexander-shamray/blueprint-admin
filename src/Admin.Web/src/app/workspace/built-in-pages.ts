import {
  API_WIDGETS,
  BROKER_WIDGETS,
  ERASURE,
  JOB_OUTPUT,
  LOGS,
  MAIL,
  MAP,
  RUNBOOKS,
  TRACE,
  TRANSCRIPT,
  SCENARIO_WIDGETS,
  STACK_WIDGETS,
  WidgetType,
} from './widgets';
import { GRID_COLUMNS, Page, WidgetInstance, Workspace, newId } from './workspace-types';

/** The schema version this console writes; `WorkspaceSchema.Current` on the host. */
export const WORKSPACE_VERSION = 2;

type Placed = readonly [WidgetType, number, number, Record<string, unknown>?];

interface BuiltInPage {
  path: string;
  label: string;
  help: string;
  layout: readonly Placed[];
}

/**
 * The console's built-in pages in their default order: each one's path, title, help and widgets. The routes, the
 * default workspace and the host's `WorkspaceDefaults` (held to this list by a drift test) all follow it. The side
 * navigation and the palette list the operator's pages instead (widget-workspace spec §7.2).
 *
 * `help` is what the assistant explains each page from (AI assistant design §6.1). The widgets own what each one shows
 * and does (`WidgetType.help`), so a page's text says what it is for and names the widgets it holds, which a spec holds
 * to its layout. It is sent in the source language; the model translates it as it answers. A spec fails an entry
 * without it.
 *
 * `layout` is the page's widgets (§5.3), each at its cell and its type's default size, in roughly the arrangement the
 * screen had. For the Stack page that is: in the left column the doctor's rows above the buttons and the job's output
 * directly under them, with the Compose services and the alerts beside that column, and the load run beside the golden
 * signals below, the stress run under it. The Scenario page keeps the run beside the order's state.
 */
export const BUILT_IN_PAGES = [
  {
    path: 'stack',
    label: 'Stack',
    help:
      'The page that runs the platform as run-locally.md does. It holds the Workstation doctor, Compose services, ' +
      'Stack controls, Alerts, Job output, Reachability, Golden signals, Load run, Stress run and Reference client ' +
      'widgets. It ' +
      'shows no log lines or traces; the Logs and Trace pages do.',
    layout: [
      [STACK_WIDGETS.doctor, 0, 0],
      [STACK_WIDGETS.services, 12, 0, { services: [] }],
      [STACK_WIDGETS.controls, 0, 10],
      [STACK_WIDGETS.alerts, 12, 12, { firingOnly: false }],
      [JOB_OUTPUT, 0, 17, { jobId: null }],
      [STACK_WIDGETS.reachability, 0, 24, { surfaces: [] }],
      [STACK_WIDGETS.signals, 0, 27, { services: [] }],
      [STACK_WIDGETS.loadRun, 14, 27],
      [STACK_WIDGETS.stressRun, 14, 35],
      [STACK_WIDGETS.client, 0, 35],
    ],
  },
  {
    path: 'logs',
    label: 'Logs',
    help:
      "The page for the services' live log output. It holds the Logs widget. To follow one request across services, " +
      'open the Trace page with its correlation id instead.',
    layout: [[LOGS, 0, 0, { services: [] }]],
  },
  {
    path: 'broker',
    label: 'Broker',
    help:
      'The page for the RabbitMQ broker. It holds the Queues, BFF projection, Dead-letter workbench, Exchanges, ' +
      'Permissions and Bindings widgets. It has no way to publish a new message.',
    layout: [
      [BROKER_WIDGETS.queues, 0, 0],
      [BROKER_WIDGETS.projection, 14, 0],
      [BROKER_WIDGETS.deadLetters, 0, 12],
      [BROKER_WIDGETS.exchanges, 0, 24],
      [BROKER_WIDGETS.permissions, 10, 24],
      [BROKER_WIDGETS.bindings, 0, 32],
    ],
  },
  {
    path: 'map',
    label: 'Map',
    help:
      'The page that shows the platform as a table of services, routes and queues. It holds the Platform map widget. ' +
      'It shows structure, not live traffic; the Trace page follows one request.',
    layout: [[MAP, 0, 0, { autoRefresh: false }]],
  },
  {
    path: 'erasure',
    label: 'Erasure',
    help:
      'The page for privacy erasure requests. It holds the Erasure widget. It does not read the customer data ' +
      'itself.',
    layout: [[ERASURE, 0, 0]],
  },
  {
    path: 'mail',
    label: 'Mail',
    help: 'The page for the mail Notifications sent. It holds the Mail widget. It sends nothing.',
    layout: [[MAIL, 0, 0, { correlationId: '' }]],
  },
  {
    path: 'requests',
    label: 'API',
    help:
      'The page for calling the platform by hand through the gateway. It holds the API request and API history ' +
      'widgets.',
    layout: [
      [API_WIDGETS.request, 0, 0],
      [API_WIDGETS.history, 16, 0],
    ],
  },
  {
    path: 'trace',
    label: 'Trace',
    help:
      'The page for following one request across the services by its correlation id. It holds the Trace widget, and ' +
      'a correlation id in a response, a log line or a mail opens it here. It does not list traces.',
    layout: [[TRACE, 0, 0, { correlationId: '', window: '15m', follow: true }]],
  },
  {
    path: 'scenario',
    label: 'Scenario',
    help:
      'The page for the end-to-end scenario. It holds the Scenario and Saga strip widgets, and it holds the stack ' +
      'while the scenario runs.',
    layout: [
      [SCENARIO_WIDGETS.scenario, 0, 0],
      [SCENARIO_WIDGETS.saga, 16, 0],
    ],
  },
  {
    path: 'runbooks',
    label: 'Runbooks',
    help:
      "The page for the backend's runbooks, for an incident or a game day. It holds the Runbooks widget. A step " +
      'that changes the platform asks to be confirmed first.',
    layout: [[RUNBOOKS, 0, 0, { runbook: null }]],
  },
  {
    path: 'transcript',
    label: 'Transcript',
    help:
      'The page for what the console ran, as the lines that would repeat it. It holds the Transcript widget. It ' +
      'changes nothing.',
    layout: [[TRANSCRIPT, 0, 0]],
  },
] as const satisfies readonly BuiltInPage[];

export type ScreenPath = (typeof BUILT_IN_PAGES)[number]['path'];

export function isScreenPath(value: unknown): value is ScreenPath {
  return typeof value === 'string' && BUILT_IN_PAGES.some((p) => p.path === value);
}

const LAYOUTS: Record<string, readonly Placed[]> = Object.fromEntries(BUILT_IN_PAGES.map((p) => [p.path, p.layout]));

/** The screens whose built-in page holds a widget of `type` by default, in the pages' order; none for a type added by hand. */
export function builtInScreensHolding(type: string): ScreenPath[] {
  return BUILT_IN_PAGES.filter((p) => p.layout.some(([kind]) => kind.type === type)).map((p) => p.path);
}

/**
 * A built-in page's widgets as the console ships them (widget-workspace spec §5.3). A built-in page the operator has
 * not changed is saved as `widgets: null` and built from here, so a later phase that changes a default reaches every
 * untouched page with no migration.
 */
export function builtInWidgets(screen: string): WidgetInstance[] {
  // A page built in by a newer console names a screen this one does not ship, so it has no defaults here (§6.3).
  if (!isScreenPath(screen)) return [];
  return LAYOUTS[screen].map(([kind, x, y, config]) => ({
    id: `builtin-${screen}-${kind.type}`,
    type: kind.type,
    x,
    y,
    cols: kind.defaultSize.cols,
    rows: kind.defaultSize.rows,
    config: config ?? {},
  }));
}

/** The type phase 6 retired (§10): one screen rendered whole, its screen in `config.screen`. */
const LEGACY_SCREEN_TYPE = 'legacy';

/** A workspace with its legacy screen widgets replaced, and the titles of the pages that changed. */
export interface Retired {
  workspace: Workspace;
  pages: string[];
}

/**
 * Every `legacy` instance a saved page still holds, replaced by its screen's default widgets (widget-workspace spec
 * §5.3, phase 6). They go inside the rectangle it occupied, stacked in their default order at their default heights, as
 * wide as the rectangle allows down to each type's minimum. The rectangle grows downwards as far as the stack needs,
 * and what sat below it moves down by as much. Where a type's minimum is wider than the rectangle and the stack would
 * then reach a neighbour beside it, the stack goes under every widget on the page instead, so nothing overlaps either
 * way. A screen this console does not name was rendered as
 * the first one, so it becomes that screen's widgets. Only a page the operator saved can hold one: a built-in page as
 * shipped is built from `LAYOUTS`.
 */
export function retireLegacy(workspace: Workspace): Retired {
  const pages: string[] = [];
  const next = workspace.pages.map((page) => {
    if (!page.widgets?.some((w) => w.type === LEGACY_SCREEN_TYPE)) return page;
    pages.push(page.title);
    return { ...page, widgets: replaceLegacy(page.widgets) };
  });
  return { workspace: pages.length === 0 ? workspace : { ...workspace, pages: next }, pages };
}

function replaceLegacy(widgets: WidgetInstance[]): WidgetInstance[] {
  let placed = widgets;
  for (;;) {
    const legacy = placed.filter((w) => w.type === LEGACY_SCREEN_TYPE).sort((a, b) => a.y - b.y || a.x - b.x)[0];
    if (!legacy) return placed;
    const stored = legacy.config['screen'];
    const screen = isScreenPath(stored) ? stored : BUILT_IN_PAGES[0].path;
    const stack = (top: number): WidgetInstance[] => {
      let y = top;
      return LAYOUTS[screen].map(([kind, , , config]): WidgetInstance => {
        const cols = Math.min(GRID_COLUMNS, Math.max(kind.minSize.cols, Math.min(kind.defaultSize.cols, legacy.cols)));
        const widget = {
          id: newId('widget'),
          type: kind.type,
          x: Math.min(legacy.x, GRID_COLUMNS - cols),
          y,
          cols,
          rows: kind.defaultSize.rows,
          config: structuredClone(config ?? {}),
        };
        y += widget.rows;
        return widget;
      });
    };
    const others = placed.filter((w) => w !== legacy);
    const inPlace = stack(legacy.y);
    const bottom = legacy.y + legacy.rows;
    const grown = Math.max(0, Math.max(...inPlace.map((w) => w.y + w.rows)) - bottom);
    const moved = new Map(others.map((w) => [w, w.y >= bottom ? { ...w, y: w.y + grown } : w]));
    if (inPlace.every((s) => others.every((w) => !overlaps(s, moved.get(w)!)))) {
      placed = placed.flatMap((w) => (w === legacy ? inPlace : [moved.get(w)!]));
    } else {
      // A type wider than the rectangle would reach a neighbour beside it, so the stack goes under everything instead.
      const below = stack(Math.max(0, ...others.map((w) => w.y + w.rows)));
      placed = placed.flatMap((w) => (w === legacy ? below : [w]));
    }
  }
}

function overlaps(a: WidgetInstance, b: WidgetInstance): boolean {
  return a.x < b.x + b.cols && b.x < a.x + a.cols && a.y < b.y + b.rows && b.y < a.y + a.rows;
}

/** The widgets a page shows: its own, or its built-in defaults while it is unchanged. */
export function widgetsOf(page: Page): WidgetInstance[] {
  return page.widgets ?? (page.builtIn ? builtInWidgets(page.builtIn) : []);
}

/** Whether Reset page has anything to put back: a built-in page's own widgets, or a title the operator gave it. */
export function differsFromBuiltIn(page: Page): boolean {
  return page.builtIn !== null && (page.widgets !== null || page.title !== builtInTitle(page.builtIn));
}

/** The title a built-in page ships with. */
export function builtInTitle(screen: string): string {
  return BUILT_IN_PAGES.find((p) => p.path === screen)?.label ?? screen;
}

/**
 * The workspace before anything is saved: the eleven built-in pages in the pages' order. The host serves the same
 * document from `WorkspaceDefaults` when its file is missing; this copy is what the SPA shows when the host cannot
 * be read at all.
 */
export function defaultWorkspace(): Workspace {
  return {
    version: WORKSPACE_VERSION,
    pages: BUILT_IN_PAGES.map((s) => ({ id: s.path, title: s.label, builtIn: s.path, hidden: false, widgets: null })),
    activePageId: BUILT_IN_PAGES[0].path,
  };
}

/**
 * Every built-in page BUILT_IN_PAGES names, in place: a saved workspace that lacks one (an older console's, or one a later
 * screen joined) gains it at the end, so a built-in page is always one click away.
 */
export function withEveryBuiltIn(workspace: Workspace): Workspace {
  const present = new Set(workspace.pages.map((p) => p.builtIn));
  const missing = defaultWorkspace().pages.filter((p) => !present.has(p.builtIn));
  return missing.length === 0 ? workspace : { ...workspace, pages: [...workspace.pages, ...missing] };
}
