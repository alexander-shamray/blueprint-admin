import { SCREENS, ScreenPath, isScreenPath } from '../screens';
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

type Placed = [WidgetType, number, number, Record<string, unknown>?];

/**
 * Each built-in page's widgets (§5.3), each at its cell and its type's default size, in roughly the arrangement the
 * screen had. For the Stack page that is: in the left column the doctor's rows above the buttons and the job's output
 * directly under them, with the Compose services and the alerts beside that column, and the load run beside the golden
 * signals below. The Scenario page keeps the run beside the order's state. Typed by SCREENS, so a screen added there
 * without a layout here does not compile.
 */
const BUILT_IN_LAYOUTS: Record<ScreenPath, readonly Placed[]> = {
  requests: [
    [API_WIDGETS.request, 0, 0],
    [API_WIDGETS.history, 16, 0],
  ],
  trace: [[TRACE, 0, 0, { correlationId: '', window: '15m', follow: true }]],
  runbooks: [[RUNBOOKS, 0, 0, { runbook: null }]],
  transcript: [[TRANSCRIPT, 0, 0]],
  broker: [
    [BROKER_WIDGETS.queues, 0, 0],
    [BROKER_WIDGETS.projection, 14, 0],
    [BROKER_WIDGETS.deadLetters, 0, 12],
    [BROKER_WIDGETS.exchanges, 0, 24],
    [BROKER_WIDGETS.permissions, 10, 24],
    [BROKER_WIDGETS.bindings, 0, 32],
  ],
  erasure: [[ERASURE, 0, 0]],
  logs: [[LOGS, 0, 0, { services: [] }]],
  mail: [[MAIL, 0, 0, { correlationId: '' }]],
  map: [[MAP, 0, 0, { autoRefresh: false }]],
  scenario: [
    [SCENARIO_WIDGETS.scenario, 0, 0],
    [SCENARIO_WIDGETS.saga, 16, 0],
  ],
  stack: [
    [STACK_WIDGETS.doctor, 0, 0],
    [STACK_WIDGETS.services, 12, 0, { services: [] }],
    [STACK_WIDGETS.controls, 0, 10],
    [STACK_WIDGETS.alerts, 12, 12, { firingOnly: false }],
    [JOB_OUTPUT, 0, 17, { jobId: null }],
    [STACK_WIDGETS.reachability, 0, 24, { surfaces: [] }],
    [STACK_WIDGETS.signals, 0, 27, { services: [] }],
    [STACK_WIDGETS.loadRun, 14, 27],
    [STACK_WIDGETS.client, 0, 35],
  ],
};

/**
 * A built-in page's widgets as the console ships them (widget-workspace spec §5.3). A built-in page the operator has
 * not changed is saved as `widgets: null` and built from here, so a later phase that changes a default reaches every
 * untouched page with no migration.
 */
export function builtInWidgets(screen: string): WidgetInstance[] {
  // A page built in by a newer console names a screen this one does not ship, so it has no defaults here (§6.3).
  if (!isScreenPath(screen)) return [];
  return BUILT_IN_LAYOUTS[screen].map(([kind, x, y, config]) => ({
    id: `builtin-${screen}-${kind.type}`,
    type: kind.type,
    x,
    y,
    cols: kind.defaultSize.cols,
    rows: kind.defaultSize.rows,
    config: config ?? {},
  }));
}

/** The type phase 6 retired (§10): one of the eleven screens rendered whole, its screen in `config.screen`. */
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
 * and what sat below it moves down by as much, so nothing overlaps. A screen this console does not name was rendered as
 * the first one, so it becomes that screen's widgets. Only a page the operator saved can hold one: a built-in page as
 * shipped is built from `BUILT_IN_LAYOUTS`.
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
    const screen = isScreenPath(stored) ? stored : SCREENS[0].path;
    let y = legacy.y;
    const stacked = BUILT_IN_LAYOUTS[screen].map(([kind, , , config]): WidgetInstance => {
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
    const bottom = legacy.y + legacy.rows;
    const grown = Math.max(0, y - bottom);
    placed = placed.flatMap((w) => (w === legacy ? stacked : [w.y >= bottom ? { ...w, y: w.y + grown } : w]));
  }
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
  return SCREENS.find((s) => s.path === screen)?.label ?? screen;
}

/**
 * The workspace before anything is saved: the eleven built-in pages in SCREENS' order. The host serves the same
 * document from `WorkspaceDefaults` when its file is missing; this copy is what the SPA shows when the host cannot
 * be read at all.
 */
export function defaultWorkspace(): Workspace {
  return {
    version: WORKSPACE_VERSION,
    pages: SCREENS.map((s) => ({ id: s.path, title: s.label, builtIn: s.path, hidden: false, widgets: null })),
    activePageId: SCREENS[0].path,
  };
}

/**
 * Every built-in page SCREENS names, in place: a saved workspace that lacks one (an older console's, or one a later
 * screen joined) gains it at the end, so a built-in page is always one click away.
 */
export function withEveryBuiltIn(workspace: Workspace): Workspace {
  const present = new Set(workspace.pages.map((p) => p.builtIn));
  const missing = defaultWorkspace().pages.filter((p) => !present.has(p.builtIn));
  return missing.length === 0 ? workspace : { ...workspace, pages: [...workspace.pages, ...missing] };
}
