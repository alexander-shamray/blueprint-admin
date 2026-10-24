import { SCREENS } from '../screens';
import { WIDGETS, WidgetType, widgetType } from '../workspace/widgets';
import { Page, WidgetInstance } from '../workspace/workspace-types';
import { ClientToolRegistry, ToolOutcome, viewTool } from './client-tools';

/** What the workspace tools need of the console, so the tools themselves are plain functions a spec can drive. */
export interface WorkspaceToolsHost {
  pages(): readonly Page[];
  currentPage(): Page | null;
  /** The widgets a page shows: its own, or the built-in defaults of a page not yet changed. */
  widgetsOf(page: Page): readonly WidgetInstance[];
  /** Routes the console to a page; false when it did not navigate. */
  open(page: Page): Promise<boolean>;
  /** Scrolls to and outlines a widget instance, waiting for it to be drawn; false when it never was. */
  highlight(instanceId: string): Promise<boolean>;
  /** What a correlation-id link does: re-point a following Trace widget, or open the Trace page. */
  followTrace(correlationId: string): void;
}

/** The backend's adoption rule for a correlation id (`CorrelationId.IsAdoptable` on the host). */
const CORRELATION_ID = /^[A-Za-z0-9_-]{1,128}$/;

/** How long a pageless `highlight_widget` waits for the page being shown to hold the widget, and how often it looks. */
export const SETTLE_MS = 3000;
const SETTLE_POLL_MS = 100;

/** The prefix a widget type's catalogue id carries, which a tool accepts so the model can pass the id it was given. */
const WIDGET_PREFIX = 'widget:';

function same(a: string, b: string): boolean {
  return a.trim().toLowerCase() === b.trim().toLowerCase();
}

/**
 * A page by what the model names: its id, its title as shown, or the screen path of a built-in page. An unknown or an
 * ambiguous name is an error that lists the titles, so the model can say what exists instead of guessing.
 */
export function findPage(pages: readonly Page[], query: string): { page: Page; partial?: true } | { error: string } {
  const wanted = query.trim();
  const found =
    pages.find((p) => p.id === wanted) ??
    pages.find((p) => same(p.title, wanted)) ??
    pages.find((p) => p.builtIn !== null && same(p.builtIn, wanted));
  if (found) return { page: found };
  const partial = pages.filter((p) => wanted !== '' && p.title.toLowerCase().includes(wanted.toLowerCase()));
  if (partial.length === 1) return { page: partial[0], partial: true };
  const titles = pages.map((p) => `"${p.title}"`).join(', ');
  return {
    error:
      partial.length > 1
        ? `"${query}" matches more than one page: ${partial.map((p) => `"${p.title}"`).join(', ')}.`
        : `There is no page "${query}". The pages are ${titles}.`,
  };
}

/** A widget type by its type, its catalogue id (`widget:<type>`) or its title. */
export function findWidgetType(query: string): WidgetType | undefined {
  const wanted = query.trim();
  const type = wanted.startsWith(WIDGET_PREFIX) ? wanted.slice(WIDGET_PREFIX.length) : wanted;
  return widgetType(type) ?? WIDGETS.find((w) => same(w.title, wanted));
}

/** A widget instance on a page by its id, else the first of a type the query names. */
export function findWidget(widgets: readonly WidgetInstance[], query: string): WidgetInstance | undefined {
  const byId = widgets.find((w) => w.id === query.trim());
  if (byId) return byId;
  const kind = findWidgetType(query);
  return kind ? widgets.find((w) => w.type === kind.type) : undefined;
}

const text = (description: string): Record<string, unknown> => ({ type: 'string', description });

/**
 * The client's tools for the workspace (AI assistant design §6.4). Each changes only the view, never the platform:
 * routing, outlining and offering a widget the operator then adds with a click.
 */
export function registerWorkspaceTools(
  tools: ClientToolRegistry,
  host: WorkspaceToolsHost,
  settleMs = SETTLE_MS,
): void {
  const openPage = async (query: string): Promise<{ page: Page; partial?: true } | ToolOutcome> => {
    const found = findPage(host.pages(), query);
    if ('error' in found) return { ok: false, content: found.error };
    return (await host.open(found.page)) ? found : { ok: false, content: 'The console did not navigate.' };
  };

  tools.register({
    descriptor: viewTool(
      'open_page',
      'Open a page',
      'Switches the console to a workspace page, by its id or its title as shown (a built-in page keeps its screen name, such as Stack or Broker).',
      {
        type: 'object',
        properties: { page: text("The page's id or title.") },
        required: ['page'],
        additionalProperties: false,
      },
    ),
    run: async (input) => {
      const opened = await openPage(String(input['page'] ?? ''));
      if (!('page' in opened)) return opened;
      // What the model should not have to guess: that the name was a near match, or that the page is out of the navigation.
      const near = opened.partial ? ` (the only page whose title contains "${String(input['page'])}")` : '';
      const hidden = opened.page.hidden ? ' It is hidden from the navigation.' : '';
      return { ok: true, content: `Opened ${opened.page.title}${near}.${hidden}` };
    },
  });

  // The tool phase 1 shipped, kept because the catalogue's screens carry its ids: now an alias of `open_page` for the
  // built-in page of the same name (§6.1).
  tools.register({
    descriptor: viewTool(
      'open_screen',
      'Open a screen',
      "Routes the console to a screen, by the catalogue entry's id. The same as open_page for the built-in page of that name.",
      {
        type: 'object',
        properties: { path: { type: 'string', enum: SCREENS.map((s) => s.path), description: "The screen's id." } },
        required: ['path'],
        additionalProperties: false,
      },
    ),
    run: async (input) => {
      const path = String(input['path']);
      const screen = SCREENS.find((s) => s.path === path);
      if (!screen) return { ok: false, content: `There is no screen ${path}.` };
      const page = host.pages().find((p) => p.builtIn === path);
      if (!page) return { ok: false, content: `The built-in page ${screen.label} is not in this workspace.` };
      return (await host.open(page))
        ? { ok: true, content: `Opened ${screen.label} at /${screen.path}.` }
        : { ok: false, content: 'The console did not navigate.' };
    },
  });

  tools.register({
    descriptor: viewTool(
      'highlight_widget',
      'Show a widget',
      'Scrolls to a widget on a page and outlines it for a few seconds. Name the widget by its type, its catalogue id (widget:<type>), its title or its instance id, and the page it is on; with no page, the page being shown.',
      {
        type: 'object',
        properties: {
          widget: text('The widget type, catalogue id, title or instance id.'),
          page: text('The page the widget is on, by id or title; the page being shown when left out.'),
        },
        required: ['widget'],
        additionalProperties: false,
      },
    ),
    run: async (input) => {
      const pageQuery = typeof input['page'] === 'string' && input['page'].trim() !== '' ? input['page'] : null;
      const query = String(input['widget'] ?? '');
      let page: Page | null;
      let widget: WidgetInstance | undefined;
      if (pageQuery !== null) {
        const found = findPage(host.pages(), pageQuery);
        if ('error' in found) return { ok: false, content: found.error };
        page = found.page;
        widget = findWidget(host.widgetsOf(page), query);
      } else {
        // The page being shown, which a navigation asked for in the same round may not have reached yet: the client
        // starts each call as it arrives, so a miss waits for the page to settle before it is one.
        const deadline = Date.now() + settleMs;
        for (;;) {
          page = host.currentPage();
          widget = page ? findWidget(host.widgetsOf(page), query) : undefined;
          if (widget || Date.now() >= deadline) break;
          await new Promise<void>((resolve) => setTimeout(resolve, SETTLE_POLL_MS));
        }
      }
      if (!page) return { ok: false, content: 'No page is being shown.' };
      if (!widget)
        return { ok: false, content: `The page "${page.title}" holds no widget "${String(input['widget'])}".` };
      return (await host.highlight(widget.id))
        ? {
            ok: true,
            content: `Outlined the ${widgetType(widget.type)?.title ?? widget.type} widget on "${page.title}".`,
          }
        : { ok: false, content: `The widget is not on screen: open the page "${page.title}" first.` };
    },
  });

  tools.register({
    descriptor: viewTool(
      'propose_widget',
      'Offer to add a widget',
      'Offers the operator an Add button for a widget type, shown under your reply. The widget is added only if the operator clicks it, to the page being shown. Use it when a widget would answer the question and no page holds it.',
      {
        type: 'object',
        properties: { type: text('The widget type, catalogue id (widget:<type>) or title.') },
        required: ['type'],
        additionalProperties: false,
      },
    ),
    run: (input) => {
      const kind = findWidgetType(String(input['type'] ?? ''));
      return kind
        ? {
            ok: true,
            content: `Offered the operator the ${kind.title} widget; it is added only if they click the button.`,
          }
        : { ok: false, content: `There is no widget type "${String(input['type'])}".` };
    },
  });

  tools.register({
    descriptor: viewTool(
      'follow_correlation',
      'Follow a correlation id',
      'Opens a correlation id in Trace: a Trace widget on the page that follows the last Trace link is pointed at it, else the Trace page opens.',
      {
        type: 'object',
        properties: { correlationId: text('A correlation id: 1 to 128 letters, digits, "-" or "_".') },
        required: ['correlationId'],
        additionalProperties: false,
      },
    ),
    run: (input) => {
      const id = String(input['correlationId'] ?? '');
      if (!CORRELATION_ID.test(id)) {
        return { ok: false, content: 'A correlation id is 1 to 128 letters, digits, "-" or "_".' };
      }
      host.followTrace(id);
      return { ok: true, content: `Opened the trace for ${id}.` };
    },
  });
}
