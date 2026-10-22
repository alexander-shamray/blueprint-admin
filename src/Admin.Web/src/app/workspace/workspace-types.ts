/**
 * The saved workspace (widget-workspace spec §4.1). The host validates this shape and its bounds
 * (`WorkspaceSchema` in src/Admin.Host/Workspace/WorkspaceSchema.cs owns the numbers below); each widget type
 * validates its own `config` (§6.3).
 */
export interface Workspace {
  version: number;
  pages: Page[];
  activePageId: string | null;
}

export interface Page {
  id: string;
  title: string;
  /** A screen's path for the eleven built-in pages, else null. */
  builtIn: string | null;
  /** A built-in page hides rather than deletes. */
  hidden: boolean;
  /** Null: a built-in page as shipped, built from the console's defaults (§5.3). */
  widgets: WidgetInstance[] | null;
}

export interface WidgetInstance {
  id: string;
  type: string;
  x: number;
  y: number;
  cols: number;
  rows: number;
  config: Record<string, unknown>;
}

/** The grid's width in cells; `WorkspaceSchema.GridColumns` on the host. */
export const GRID_COLUMNS = 24;

/** The tallest a widget may be, in rows; `WorkspaceSchema.MaxRows` on the host. */
export const MAX_ROWS = 100;

/**
 * How many rows a page's grid has, the SPA's own limit: the host bounds a widget's height, not where it starts.
 * gridster's default is 100, and a widget it cannot place is hidden without a word, so the grid is given this many
 * and "Add widget" refuses a widget that would end below it.
 */
export const PAGE_ROWS = 2000;

/** A fresh id for a page or a widget: unique across the document, which the host requires of widget ids. */
export function newId(prefix: string): string {
  return `${prefix}-${crypto.randomUUID()}`;
}
