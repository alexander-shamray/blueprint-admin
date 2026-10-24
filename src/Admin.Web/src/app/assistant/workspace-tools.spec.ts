import { builtInWidgets } from '../workspace/built-in-pages';
import { Page, WidgetInstance } from '../workspace/workspace-types';
import { ClientToolRegistry } from './client-tools';
import { WorkspaceToolsHost, findPage, findWidget, findWidgetType, registerWorkspaceTools } from './workspace-tools';

const page = (
  id: string,
  title: string,
  builtIn: string | null = null,
  widgets: WidgetInstance[] | null = [],
): Page => ({
  id,
  title,
  builtIn,
  hidden: false,
  widgets,
});

const widget = (id: string, type: string): WidgetInstance => ({ id, type, x: 0, y: 0, cols: 8, rows: 8, config: {} });

const PAGES: Page[] = [
  page('p-stack', 'Stack', 'stack', null),
  page('p-broker', 'Broker', 'broker', null),
  page('p-mine', 'Daily checks', null, [widget('w-note', 'note'), widget('w-queues', 'queues')]),
  page('p-other', 'Daily notes', null, []),
];

function setup(start: Page | null = PAGES[2], settleMs = 0) {
  const calls = { opened: [] as string[], highlighted: [] as string[], followed: [] as string[] };
  let current = start;
  const host: WorkspaceToolsHost = {
    pages: () => PAGES,
    currentPage: () => current,
    widgetsOf: (p) => p.widgets ?? (p.builtIn ? builtInWidgets(p.builtIn) : []),
    open: (p) => {
      calls.opened.push(p.id);
      return Promise.resolve(true);
    },
    highlight: (id) => {
      calls.highlighted.push(id);
      return Promise.resolve(true);
    },
    followTrace: (id) => calls.followed.push(id),
  };
  const tools = new ClientToolRegistry();
  registerWorkspaceTools(tools, host, settleMs);
  return { tools, host, calls, show: (p: Page | null) => (current = p) };
}

describe('findPage', () => {
  it('reads an id, a title in any case, and the screen path of a built-in page', () => {
    expect(findPage(PAGES, 'p-mine')).toEqual({ page: PAGES[2] });
    expect(findPage(PAGES, ' daily CHECKS ')).toEqual({ page: PAGES[2] });
    expect(findPage(PAGES, 'broker')).toEqual({ page: PAGES[1] });
  });

  it('takes a part of a title only when exactly one page has it', () => {
    expect(findPage(PAGES, 'checks')).toEqual({ page: PAGES[2], partial: true });
    expect(findPage(PAGES, 'daily')).toEqual({ error: expect.stringContaining('more than one page') });
  });

  it('says which pages exist when the name is none of them', () => {
    expect(findPage(PAGES, 'nowhere')).toEqual({ error: expect.stringContaining('"Stack", "Broker", "Daily checks"') });
    expect(findPage(PAGES, '')).toHaveProperty('error');
  });
});

describe('finding a widget', () => {
  it('reads a type, a catalogue id and a title as the same widget type', () => {
    expect(findWidgetType('queues')?.type).toBe('queues');
    expect(findWidgetType('widget:queues')?.type).toBe('queues');
    expect(findWidgetType('Dead-letter workbench')?.type).toBe('dead-letters');
    expect(findWidgetType('nonsense')).toBeUndefined();
  });

  it('finds an instance by id, else the first of the type', () => {
    const widgets = [widget('w1', 'note'), widget('w2', 'queues'), widget('w3', 'queues')];

    expect(findWidget(widgets, 'w3')?.id).toBe('w3');
    expect(findWidget(widgets, 'widget:queues')?.id).toBe('w2');
    expect(findWidget(widgets, 'logs')).toBeUndefined();
  });
});

describe('the workspace client tools (AI assistant design §6.4)', () => {
  it('are all view tools, read-only in the platform sense', () => {
    const { tools } = setup();

    expect(
      tools
        .descriptors()
        .map((d) => d.name)
        .sort(),
    ).toEqual(['follow_correlation', 'highlight_widget', 'open_page', 'open_screen', 'propose_widget']);
    expect(
      tools
        .descriptors()
        .every((d) => d.side === 'client' && d.annotations.readOnlyHint && !d.annotations.destructiveHint),
    ).toBe(true);
  });

  it('opens a page by title, and says what exists when there is none', async () => {
    const { tools, calls } = setup();

    expect(await tools.run('open_page', { page: 'Broker' })).toEqual({ ok: true, content: 'Opened Broker.' });
    const missing = await tools.run('open_page', { page: 'Nowhere' });

    expect(calls.opened).toEqual(['p-broker']);
    expect(missing.ok).toBe(false);
    expect(missing.content).toContain('"Daily checks"');
  });

  it('says when a page was matched by part of its title, and when it is hidden from the navigation', async () => {
    const { tools } = setup();
    PAGES[3].hidden = true;
    try {
      const near = await tools.run('open_page', { page: 'checks' });
      const hidden = await tools.run('open_page', { page: 'Daily notes' });

      expect(near.content).toBe('Opened Daily checks (the only page whose title contains "checks").');
      expect(hidden.content).toBe('Opened Daily notes. It is hidden from the navigation.');
    } finally {
      PAGES[3].hidden = false;
    }
  });

  it('keeps open_screen as the alias of open_page for the built-in page of that name', async () => {
    const { tools, calls } = setup();

    expect(await tools.run('open_screen', { path: 'stack' })).toEqual({ ok: true, content: 'Opened Stack at /stack.' });
    expect((await tools.run('open_screen', { path: 'nope' })).ok).toBe(false);
    expect(calls.opened).toEqual(['p-stack']);
  });

  it('outlines a widget on the page being shown, or on the page it is told', async () => {
    const { tools, calls } = setup();

    expect((await tools.run('highlight_widget', { widget: 'queues' })).ok).toBe(true);
    expect((await tools.run('highlight_widget', { widget: 'widget:queues', page: 'Broker' })).ok).toBe(true);

    expect(calls.highlighted).toEqual(['w-queues', 'builtin-broker-queues']);
  });

  it('says so when the page holds no such widget, and when the widget never appears', async () => {
    const { tools, host } = setup();

    expect((await tools.run('highlight_widget', { widget: 'logs' })).content).toContain('holds no widget "logs"');
    expect((await tools.run('highlight_widget', { widget: 'queues', page: 'Elsewhere' })).ok).toBe(false);

    host.highlight = () => Promise.resolve(false);
    const hidden = await tools.run('highlight_widget', { widget: 'queues' });
    expect(hidden.ok).toBe(false);
    expect(hidden.content).toContain('open the page');
  });

  it('waits, with no page named, for the page being shown to change when open_page was asked in the same round', async () => {
    vi.useFakeTimers();
    try {
      const { tools, calls, show } = setup(PAGES[0], 3000);

      // The navigation lands a little after highlight_widget starts, as the client starts each call as it arrives.
      const outcome = tools.run('highlight_widget', { widget: 'widget:queues' });
      await vi.advanceTimersByTimeAsync(250);
      show(PAGES[1]);
      await vi.advanceTimersByTimeAsync(200);

      expect((await outcome).ok).toBe(true);
      expect(calls.highlighted).toEqual(['builtin-broker-queues']);
    } finally {
      vi.useRealTimers();
    }
  });

  it('gives up after the wait when the page being shown never holds the widget', async () => {
    vi.useFakeTimers();
    try {
      const { tools } = setup(PAGES[0], 500);

      const outcome = tools.run('highlight_widget', { widget: 'widget:queues' });
      await vi.advanceTimersByTimeAsync(700);

      expect((await outcome).content).toContain('holds no widget');
    } finally {
      vi.useRealTimers();
    }
  });

  it('has nothing to highlight when no page is showing', async () => {
    const { tools } = setup(null);

    expect((await tools.run('highlight_widget', { widget: 'queues' })).content).toBe('No page is being shown.');
  });

  it('offers a known widget type, adds nothing itself, and refuses an unknown one', async () => {
    const { tools, calls } = setup();

    expect((await tools.run('propose_widget', { type: 'widget:note' })).content).toContain('only if they click');
    expect((await tools.run('propose_widget', { type: 'hologram' })).ok).toBe(false);
    expect(calls.opened).toEqual([]);
  });

  it('opens a correlation id in Trace, and refuses one the platform would not adopt', async () => {
    const { tools, calls } = setup();

    expect((await tools.run('follow_correlation', { correlationId: 'order-1_a' })).ok).toBe(true);
    expect((await tools.run('follow_correlation', { correlationId: 'a b' })).ok).toBe(false);
    expect((await tools.run('follow_correlation', { correlationId: 'x'.repeat(129) })).ok).toBe(false);
    expect(calls.followed).toEqual(['order-1_a']);
  });
});
