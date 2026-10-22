import { SCREENS } from '../screens';
import { JOB_OUTPUT, LEGACY_SCREEN, STACK_WIDGETS, WidgetType } from './widgets';
import { Page, WidgetInstance, Workspace } from './workspace-types';

/** The schema version this console writes; `WorkspaceSchema.Current` on the host. */
export const WORKSPACE_VERSION = 1;

type Placed = [WidgetType, number, number, Record<string, unknown>?];

/**
 * The built-in pages rebuilt from widgets so far (§10), each widget at its cell and its type's default size, in
 * roughly the arrangement the screen had. For the Stack page that is the screens table's (spec §6): in the left
 * column the doctor's rows above the buttons and the job's output directly under them, with the Compose services and
 * the alerts beside that column, and the load run beside the golden signals below. A page not listed is still its
 * screen in one legacy widget.
 */
const BUILT_IN_LAYOUTS: Partial<Record<string, readonly Placed[]>> = {
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
  const layout = BUILT_IN_LAYOUTS[screen];
  if (layout) {
    return layout.map(([kind, x, y, config]) => ({
      id: `builtin-${screen}-${kind.type}`,
      type: kind.type,
      x,
      y,
      cols: kind.defaultSize.cols,
      rows: kind.defaultSize.rows,
      config: config ?? {},
    }));
  }
  return [
    {
      id: `builtin-${screen}-screen`,
      type: LEGACY_SCREEN.type,
      x: 0,
      y: 0,
      cols: LEGACY_SCREEN.defaultSize.cols,
      rows: LEGACY_SCREEN.defaultSize.rows,
      config: { screen },
    },
  ];
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
