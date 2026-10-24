import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { PlatformMapView, StackView } from '../../../core/host/host-types';
import { BrokerFeed } from '../../../features/broker/broker-feed';
import { view as map } from '../../../features/map/map-testing';
import { StackFeed } from '../../../features/stack/stack-feed';
import { WIDGET_CONTEXT } from '../../widget-context';
import { WidgetSessionStore } from '../../widget-session-store';
import { WorkspaceService } from '../../workspace-service';
import { WidgetInstance } from '../../workspace-types';
import { GRAPH_FACTORY, GraphEvents } from './graph-loader';
import { Topology } from './topology-graph';
import { TopologyWidget } from './topology-widget';

const stack = {
  backend: { reachable: true, error: null, services: [] },
  reachability: [{ name: 'gateway', url: 'http://localhost:5000/health/ready', up: true, status: 200 }],
} as unknown as StackView;

function widget(id: string, type: string, config: Record<string, unknown>): WidgetInstance {
  return { id, type, x: 0, y: 0, cols: 8, rows: 8, config };
}

describe('TopologyWidget', () => {
  let host: { platformMap: ReturnType<typeof vi.fn> };
  let drawing: {
    show: ReturnType<typeof vi.fn>;
    relayout: ReturnType<typeof vi.fn>;
    destroy: ReturnType<typeof vi.fn>;
  };
  let events: GraphEvents;
  let create: ReturnType<typeof vi.fn>;
  let workspace: {
    currentPage: ReturnType<typeof vi.fn>;
    configureWidget: ReturnType<typeof vi.fn>;
    pages: ReturnType<typeof vi.fn>;
    pageLink: ReturnType<typeof vi.fn>;
  };
  let router: { navigateByUrl: ReturnType<typeof vi.fn>; navigate: ReturnType<typeof vi.fn> };
  let broker: { workbenches: ReturnType<typeof signal<number>>; requestOpen: ReturnType<typeof vi.fn> };
  let onPage: WidgetInstance[];

  beforeEach(() => {
    vi.useFakeTimers();
    host = { platformMap: vi.fn(() => of(map)) };
    drawing = { show: vi.fn(), relayout: vi.fn(), destroy: vi.fn() };
    create = vi.fn(async (_container: HTMLElement, e: GraphEvents) => {
      events = e;
      return drawing;
    });
    onPage = [];
    workspace = {
      currentPage: vi.fn(() => ({ id: 'p1', title: 'Page', builtIn: null, hidden: false, widgets: onPage })),
      configureWidget: vi.fn(),
      pages: vi.fn(() => []),
      pageLink: vi.fn((id: string) => ['/', id]),
    };
    router = { navigateByUrl: vi.fn(() => Promise.resolve(true)), navigate: vi.fn(() => Promise.resolve(true)) };
    broker = { workbenches: signal(0), requestOpen: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: HostClient, useValue: host },
        { provide: StackFeed, useValue: { stack: { value: signal(stack), refresh: vi.fn() } } },
        { provide: BrokerFeed, useValue: broker },
        { provide: WorkspaceService, useValue: workspace },
        { provide: Router, useValue: router },
        { provide: GRAPH_FACTORY, useValue: create },
        { provide: WIDGET_CONTEXT, useValue: { instanceId: 'w-diagram', pageId: 'p1' } },
      ],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  async function render(config: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(TopologyWidget);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  const shown = (): Topology => drawing.show.mock.lastCall![0];
  const click = (el: HTMLElement, name: string): void => {
    Array.from(el.querySelectorAll<HTMLButtonElement>('ul.nodes button'))
      .find((b) => b.textContent?.trim() === name)!
      .click();
  };

  it("draws the map into the canvas, with the operator's positions", async () => {
    const fixture = await render({ positions: { 's:gateway': { x: 5, y: 6 } } });

    expect(create).toHaveBeenCalledTimes(1);
    expect(create.mock.calls[0][0]).toBe(fixture.nativeElement.querySelector('.graph'));
    expect(shown().nodes.map((n) => n.id)).toContain('q:ordering-catalog-events');
    expect(drawing.show.mock.lastCall![1]).toEqual({ 's:gateway': { x: 5, y: 6 } });
  });

  it('lists every service and queue in words, with the state a colour would say', async () => {
    const el: HTMLElement = (await render()).nativeElement;

    expect(
      Array.from(el.querySelectorAll('ul.nodes li')).map((li) => li.textContent?.replace(/\s+/g, ' ').trim()),
    ).toEqual([
      'gateway reachable',
      'ordering not probed',
      'shipping no Compose service',
      'ordering-catalog-events parked: error 1 2 · —',
    ]);
    expect(el.querySelector('.graph')?.getAttribute('aria-label')).toContain('3 services and 1 queues');
  });

  it('draws the next answer without drawing it again from scratch', async () => {
    const fixture = await render();
    const calls = drawing.show.mock.calls.length;
    host.platformMap.mockReturnValue(of({ ...map, services: map.services.filter((s) => s.name !== 'shipping') }));

    fixture.nativeElement.querySelector('button.refresh').click();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledTimes(1);
    expect(drawing.show.mock.calls.length).toBeGreaterThan(calls);
    expect(shown().nodes.some((n) => n.id === 's:shipping')).toBe(false);
  });

  it('lists a disagreement and resolves none', async () => {
    const el: HTMLElement = (await render()).nativeElement;

    expect(el.querySelector('section.disagreements li')?.textContent).toContain('mystery-queue is on the broker');
  });

  it('says the broker did not answer, and the host did not', async () => {
    host.platformMap.mockReturnValue(
      of({
        ...map,
        brokerReachable: false,
        error: 'docker is not running',
        disagreements: [],
      } satisfies PlatformMapView),
    );

    const el: HTMLElement = (await render()).nativeElement;

    expect(el.querySelector('[role="alert"]')?.textContent).toContain(
      'The broker did not answer: docker is not running',
    );
  });

  it('refreshes every 5 s from the start, and says so only until the settings switch it off', async () => {
    expect(((await render()).nativeElement as HTMLElement).querySelector('.auto')?.textContent).toContain(
      'Refreshes every 5 s',
    );
    expect(((await render({ autoRefresh: false })).nativeElement as HTMLElement).querySelector('.auto')).toBeNull();
  });

  it('says so when the drawing library could not be fetched, and still lists the nodes', async () => {
    create.mockRejectedValue(new Error('Failed to fetch dynamically imported module'));

    const el: HTMLElement = (await render()).nativeElement;

    expect(el.querySelector('[role="alert"]')?.textContent).toContain(
      'The diagram could not be drawn: Failed to fetch',
    );
    expect(el.querySelectorAll('ul.nodes li').length).toBeGreaterThan(0);
  });

  it('ends the drawing with the widget', async () => {
    const fixture = await render();

    fixture.destroy();
    expect(drawing.destroy).toHaveBeenCalledTimes(1);
  });

  describe('a click on a service', () => {
    it('points a Logs widget on the page at its Compose service', async () => {
      onPage = [widget('w-logs', 'logs', { services: [] })];
      const fixture = await render();
      const el: HTMLElement = fixture.nativeElement;

      click(el, 'ordering');
      fixture.detectChanges();

      // Session state of the Logs widget: the saved page is not written.
      expect(TestBed.inject(WidgetSessionStore).state('w-logs', 'pointed', null)()).toEqual(['ordering-api']);
      expect(workspace.configureWidget).not.toHaveBeenCalled();
      expect(el.querySelector('.notice')?.textContent).toContain('now shows ordering-api');
    });

    it('opens the Logs page when the page has no Logs widget', async () => {
      const el: HTMLElement = (await render()).nativeElement;

      click(el, 'ordering');

      expect(workspace.configureWidget).not.toHaveBeenCalled();
      expect(router.navigateByUrl).toHaveBeenCalledWith('/logs');
    });

    it("points the built-in Logs page's widget and opens that page when this page has no Logs widget", async () => {
      workspace.pages.mockReturnValue([
        {
          id: 'pg-logs',
          title: 'Logs',
          builtIn: 'logs',
          hidden: false,
          widgets: [widget('w-builtin-logs', 'logs', {})],
        },
      ]);
      const el: HTMLElement = (await render()).nativeElement;

      click(el, 'ordering');

      expect(TestBed.inject(WidgetSessionStore).state('w-builtin-logs', 'pointed', null)()).toEqual(['ordering-api']);
      expect(router.navigate).toHaveBeenCalledWith(['/', 'pg-logs']);
      expect(router.navigateByUrl).not.toHaveBeenCalled();
    });

    it('says so when the service has no Compose service to follow', async () => {
      host.platformMap.mockReturnValue(
        of({ ...map, services: [...map.services, { name: 'ghost', url: null, routes: [], queues: [] }] }),
      );
      const fixture = await render();
      const el: HTMLElement = fixture.nativeElement;

      click(el, 'ghost');
      fixture.detectChanges();

      expect(el.querySelector('.notice')?.textContent).toContain('ghost has no Compose service');
      expect(router.navigateByUrl).not.toHaveBeenCalled();
    });

    it('does the same for a click in the drawing as for one in the list', async () => {
      await render();

      events.tap('s:ordering');

      expect(router.navigateByUrl).toHaveBeenCalledWith('/logs');
    });
  });

  describe('a click on a queue', () => {
    it('opens a parked queue in the dead-letter workbench the page shows', async () => {
      broker.workbenches.set(1);
      const el: HTMLElement = (await render()).nativeElement;

      click(el, 'ordering-catalog-events');

      expect(broker.requestOpen).toHaveBeenCalledWith('ordering-catalog-events_error');
      expect(router.navigateByUrl).not.toHaveBeenCalled();
    });

    it('opens the Broker page for a parked queue when there is no workbench to open it in', async () => {
      const el: HTMLElement = (await render()).nativeElement;

      click(el, 'ordering-catalog-events');

      expect(broker.requestOpen).not.toHaveBeenCalled();
      expect(router.navigateByUrl).toHaveBeenCalledWith('/broker');
    });

    const quiet: PlatformMapView = {
      ...map,
      services: map.services.map((s) => ({ ...s, queues: s.queues.filter((q) => q.parked === null) })),
    };

    it('points a Queues widget on the page at a queue that is not parked', async () => {
      host.platformMap.mockReturnValue(of(quiet));
      onPage = [widget('w-queues', 'queues', { filter: '', parkedOnly: true, autoRefresh: true })];
      const el: HTMLElement = (await render()).nativeElement;

      click(el, 'ordering-catalog-events');

      expect(TestBed.inject(WidgetSessionStore).state('w-queues', 'pointed', null)()).toBe('ordering-catalog-events');
      expect(workspace.configureWidget).not.toHaveBeenCalled();
    });

    it("points the built-in Broker page's Queues widget and opens that page when this page has none", async () => {
      host.platformMap.mockReturnValue(of(quiet));
      workspace.pages.mockReturnValue([
        {
          id: 'pg-broker',
          title: 'Broker',
          builtIn: 'broker',
          hidden: false,
          widgets: [widget('w-builtin-queues', 'queues', {})],
        },
      ]);
      const el: HTMLElement = (await render()).nativeElement;

      click(el, 'ordering-catalog-events');

      expect(TestBed.inject(WidgetSessionStore).state('w-builtin-queues', 'pointed', null)()).toBe(
        'ordering-catalog-events',
      );
      expect(router.navigate).toHaveBeenCalledWith(['/', 'pg-broker']);
    });

    it('opens the Broker page for a queue that is not parked when the page has no Queues widget', async () => {
      host.platformMap.mockReturnValue(of(quiet));
      const el: HTMLElement = (await render()).nativeElement;

      click(el, 'ordering-catalog-events');

      expect(router.navigateByUrl).toHaveBeenCalledWith('/broker');
    });
  });

  describe('where the operator puts a node', () => {
    it("is kept in the widget's own config, beside the nodes moved before", async () => {
      await render({ autoRefresh: true, positions: { 's:gateway': { x: 1, y: 2 } } });

      events.moved('q:ordering-catalog-events', { x: 30, y: 40 });

      expect(workspace.configureWidget).toHaveBeenCalledWith('p1', 'w-diagram', {
        autoRefresh: true,
        positions: { 's:gateway': { x: 1, y: 2 }, 'q:ordering-catalog-events': { x: 30, y: 40 } },
      });
    });

    it('is forgotten by Re-layout, which lays everything out again', async () => {
      const fixture = await render({ autoRefresh: true, positions: { 's:gateway': { x: 1, y: 2 } } });

      fixture.nativeElement.querySelector('button.relayout').click();

      expect(workspace.configureWidget).toHaveBeenCalledWith('p1', 'w-diagram', { autoRefresh: true, positions: {} });
      expect(drawing.relayout).toHaveBeenCalledTimes(1);
    });
  });
});
