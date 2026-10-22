import { SCREENS } from '../screens';
import { LEGACY_SCREEN } from './widgets';
import { Page, WidgetInstance, Workspace } from './workspace-types';

/** The schema version this console writes; `WorkspaceSchema.Current` on the host. */
export const WORKSPACE_VERSION = 1;

/**
 * A built-in page's widgets as the console ships them (widget-workspace spec §5.3): in this phase, one legacy
 * screen widget across the grid. A built-in page the operator has not changed is saved as `widgets: null` and built
 * from here, so a later phase that changes a default reaches every untouched page with no migration.
 */
export function builtInWidgets(screen: string): WidgetInstance[] {
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
