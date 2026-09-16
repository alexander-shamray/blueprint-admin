import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { TraceView } from '../../core/host/host-types';
import { TracePage } from './trace-page';

const queued = {
  at: '2026-09-16T09:43:03.000Z',
  source: 'broker',
  service: 'rabbitmq',
  kind: 'Queued' as const,
  summary:
    'ordering-catalog-events: 0 messages (drained). The publish runs in a new trace — the outbox carries no trace context, so the consume side is not joinable by this correlation id.',
  traceId: null,
  link: null,
};

const view: TraceView = {
  correlationId: 'demo-trace-0001',
  window: '15m',
  reachable: true,
  error: null,
  traceIds: ['4bf92f3577b34da6a3ce929d0e0e4736'],
  tracesTruncated: false,
  warning: null,
  events: [
    {
      at: '2026-09-16T09:43:00.000Z',
      source: 'loki',
      service: 'Gateway.Api',
      kind: 'Log',
      summary: 'Request starting POST /api/v1/products',
      traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
      link: 'http://localhost:3000/explore?panes=%7B%7D',
    },
    {
      at: '2026-09-16T09:43:01.000Z',
      source: 'tempo',
      service: 'Catalog.Api',
      kind: 'Outbox',
      summary: 'INSERT catalog (120 ms)',
      traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
      link: null,
    },
    queued,
  ],
};

const empty: TraceView = { ...view, events: [queued], traceIds: [] };

function rows(fixture: { nativeElement: HTMLElement }): string[] {
  return (Array.from(fixture.nativeElement.querySelectorAll('table.timeline tbody tr')) as HTMLElement[]).map(
    (r) => r.textContent?.replace(/\s+/g, ' ').trim() ?? '',
  );
}

describe('TracePage', () => {
  let host: { trace: ReturnType<typeof vi.fn> };
  let paramMap: BehaviorSubject<ReturnType<typeof convertToParamMap>>;

  function configure(correlationId: string | null) {
    paramMap = new BehaviorSubject(convertToParamMap(correlationId === null ? {} : { correlationId }));
    TestBed.configureTestingModule({
      imports: [TracePage],
      providers: [
        provideRouter([]),
        { provide: HostClient, useValue: host },
        { provide: ActivatedRoute, useValue: { paramMap } },
      ],
    });
  }

  function render() {
    const fixture = TestBed.createComponent(TracePage);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    host = { trace: vi.fn(() => of(view)) };
  });

  it('loads the id in the route and renders a row per event in time order with the kind in words', () => {
    configure('demo-trace-0001');
    const fixture = render();

    expect(host.trace).toHaveBeenCalledWith('demo-trace-0001', '15m');
    const timeline = rows(fixture);
    expect(timeline.length).toBe(3);
    expect(timeline[0]).toContain('[log]');
    expect(timeline[0]).toContain('Request starting POST /api/v1/products');
    expect(timeline[1]).toContain('[outbox]');
    expect(timeline[2]).toContain('[queued]');
  });

  it('renders the terminal queued row last with the sentence that explains why the timeline stops', () => {
    configure('demo-trace-0001');
    const fixture = render();

    const last = rows(fixture).at(-1) ?? '';
    expect(last).toContain('ordering-catalog-events: 0 messages (drained).');
    expect(last).toContain('the outbox carries no trace context');
    expect(fixture.nativeElement.querySelector('tr.kind-Queued')).toBeTruthy();
  });

  it('links an event to Grafana Explore in a new tab, and links nothing when there is no link', () => {
    configure('demo-trace-0001');
    const fixture = render();

    const links = fixture.nativeElement.querySelectorAll('a.explore') as NodeListOf<HTMLAnchorElement>;
    expect(links.length).toBe(1);
    expect(links[0].getAttribute('target')).toBe('_blank');
    expect(links[0].getAttribute('rel')).toBe('noopener');
    expect(links[0].getAttribute('aria-label')).toContain('Grafana Explore');
  });

  it('says how many events over what window in a status line', () => {
    configure('demo-trace-0001');
    const fixture = render();

    const status = fixture.nativeElement.querySelector('[role="status"]') as HTMLElement;
    expect(status.textContent).toContain('3 events for demo-trace-0001 over the last 15m');
  });

  it('explains an empty result rather than showing a bare table', () => {
    host.trace = vi.fn(() => of(empty));
    configure('nothing-here');
    const fixture = render();

    const text = ((fixture.nativeElement.querySelector('.empty') as HTMLElement).textContent ?? '').replace(/\s+/g, ' ');
    expect(text).toContain('No log line carries correlation id');
    expect(text).toContain('may be older than the window');
    // The terminal marker still renders: the broker's state is worth knowing even with no lines.
    expect(rows(fixture).length).toBe(1);
  });

  it('shows a Grafana that did not answer as a state, not as a thrown error', () => {
    host.trace = vi.fn(() => of({ ...view, reachable: false, error: 'Loki answered 503', events: [] }));
    configure('demo-trace-0001');
    const fixture = render();

    const alert = fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('Loki answered 503');
  });

  it('keeps the last good timeline on screen behind a failed reload', () => {
    configure('demo-trace-0001');
    const fixture = render();
    expect(rows(fixture).length).toBe(3);

    host.trace = vi.fn(() => throwError(() => ({ error: { detail: 'the host fell over' } })));
    (fixture.componentInstance as TracePage).reload();
    fixture.detectChanges();

    expect((fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement).textContent).toContain(
      'the host fell over',
    );
    expect(rows(fixture).length).toBe(3);
  });

  it('loads nothing on /trace and invites an id instead', () => {
    configure(null);
    const fixture = render();

    expect(host.trace).not.toHaveBeenCalled();
    expect((fixture.nativeElement.querySelector('.empty') as HTMLElement).textContent).toContain('Enter a correlation id');
  });

  it('navigates to the id rather than loading it, so the URL is shareable', () => {
    configure(null);
    const fixture = render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    fixture.componentInstance.correlationId.set('  typed-id  ');
    fixture.nativeElement.querySelector('form').dispatchEvent(new Event('submit'));

    expect(navigate).toHaveBeenCalledWith(['/trace', 'typed-id']);
    expect(host.trace).not.toHaveBeenCalled();
  });

  it('reloads when the route param changes, because the component is reused', () => {
    configure('demo-trace-0001');
    render();

    paramMap.next(convertToParamMap({ correlationId: 'another-id' }));

    expect(host.trace).toHaveBeenCalledTimes(2);
    expect(host.trace).toHaveBeenLastCalledWith('another-id', '15m');
  });
  it('abandons an in-flight read when the route moves to another id, rather than dropping the new one', () => {
    const first = new Subject<TraceView>();
    host.trace = vi.fn((id: string) => (id === 'demo-trace-0001' ? first : of({ ...view, correlationId: 'second-id' })));
    configure('demo-trace-0001');
    const fixture = render();

    paramMap.next(convertToParamMap({ correlationId: 'second-id' }));
    fixture.detectChanges();

    expect(host.trace).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.view()?.correlationId).toBe('second-id');

    // The abandoned read answering late must not overwrite the timeline now on screen.
    first.next({ ...view, correlationId: 'demo-trace-0001' });
    first.complete();
    fixture.detectChanges();

    expect(fixture.componentInstance.view()?.correlationId).toBe('second-id');
    expect(fixture.componentInstance.loading()).toBe(false);
  });
  it('shows a 400 as the host answered it, not as the host failing to answer', () => {
    host.trace = vi.fn(() => throwError(() => ({ error: { title: 'Invalid window', detail: 'A window is a whole number and a unit.' } })));
    configure('demo-trace-0001');
    const fixture = render();

    const alert = (fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement).textContent ?? '';
    expect(alert).toContain('A window is a whole number and a unit.');
    expect(alert).not.toContain('did not answer');
  });

  it('drops the timeline when a different id fails, so one id events never sit under another id name', () => {
    configure('demo-trace-0001');
    const fixture = render();
    expect(rows(fixture).length).toBe(3);

    host.trace = vi.fn(() => throwError(() => ({ error: { detail: 'nope' } })));
    paramMap.next(convertToParamMap({ correlationId: 'second-id' }));
    fixture.detectChanges();

    expect(rows(fixture).length).toBe(0);
    expect((fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement).textContent).toContain('nope');
  });
  it('clears the timeline the moment a different id starts loading, not only when it fails', () => {
    const pending = new Subject<TraceView>();
    configure('demo-trace-0001');
    const fixture = render();
    expect(rows(fixture).length).toBe(3);

    host.trace = vi.fn(() => pending);
    paramMap.next(convertToParamMap({ correlationId: 'second-id' }));
    fixture.detectChanges();

    // Still in flight: nothing of demo-trace-0001 may remain under a URL that says second-id.
    expect(fixture.componentInstance.loading()).toBe(true);
    expect(rows(fixture).length).toBe(0);
  });
  it('keeps Reload available after a first load fails, since submitting the same route retries nothing', () => {
    host.trace = vi.fn(() => throwError(() => ({ error: { detail: 'the host fell over' } })));
    configure('demo-trace-0001');
    const fixture = render();

    expect(fixture.componentInstance.view()).toBeNull();
    const reload = fixture.nativeElement.querySelector('button.reload') as HTMLButtonElement;
    expect(reload.disabled).toBe(false);

    host.trace = vi.fn(() => of(view));
    reload.click();
    fixture.detectChanges();

    expect(rows(fixture).length).toBe(3);
    expect(fixture.nativeElement.querySelector('[role="alert"]')).toBeNull();
  });

  it('clears the previous error when the next load starts, not when it finishes', () => {
    const pending = new Subject<TraceView>();
    host.trace = vi.fn(() => throwError(() => ({ error: { detail: 'first attempt failed' } })));
    configure('demo-trace-0001');
    const fixture = render();
    expect(fixture.componentInstance.error()).toBe('first attempt failed');

    host.trace = vi.fn(() => pending);
    paramMap.next(convertToParamMap({ correlationId: 'second-id' }));
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toBeNull();
    expect(fixture.nativeElement.querySelector('[role="alert"]')).toBeNull();
  });
  it('shows a partial Tempo failure as a warning, with the timeline it did get', () => {
    host.trace = vi.fn(() => of({ ...view, warning: '1 of 2 traces could not be read from Tempo: Tempo answered 404' }));
    configure('demo-trace-0001');
    const fixture = render();

    const warning = fixture.nativeElement.querySelector('p.warning') as HTMLElement;
    expect(warning.getAttribute('role')).toBe('status');
    expect(warning.textContent).toContain('could not be read from Tempo');
    expect(rows(fixture).length).toBe(3);
  });
  it('does not call a missing Loki datasource an outage', () => {
    host.trace = vi.fn(() => of({ ...view, reachable: false, error: 'Grafana has no Loki datasource.', events: [] }));
    configure('demo-trace-0001');
    const fixture = render();

    const alert = (fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement).textContent ?? '';
    expect(alert).toContain('Grafana has no Loki datasource.');
    expect(alert).not.toContain('did not answer');
  });
  it('keeps the last good timeline when Grafana goes down during a reload', () => {
    configure('demo-trace-0001');
    const fixture = render();
    expect(rows(fixture).length).toBe(3);

    host.trace = vi.fn(() => of({ ...view, reachable: false, error: 'Loki answered 503', events: [] }));
    fixture.componentInstance.reload();
    fixture.detectChanges();

    // The same event, whether it arrives thrown or as reachable:false, is shown the same way.
    expect((fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement).textContent).toContain('Loki answered 503');
    expect(rows(fixture).length).toBe(3);
  });
});
