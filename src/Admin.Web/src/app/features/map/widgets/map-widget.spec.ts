import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { MapFeed } from '../map-feed';
import { view } from '../map-testing';
import { MapWidget } from './map-widget';

describe('MapWidget', () => {
  let host: { platformMap: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    vi.useFakeTimers();
    host = { platformMap: vi.fn(() => of(view)) };
    TestBed.configureTestingModule({ providers: [{ provide: HostClient, useValue: host }] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function render(config: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(MapWidget);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    return fixture;
  }

  it('draws a section per service with its routes and queues, and names a worker as having no API', () => {
    const el: HTMLElement = render().nativeElement;

    expect(Array.from(el.querySelectorAll('section.service h2')).map((h) => h.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'gateway http://localhost:5000',
      'ordering http://localhost:5101',
      'shipping worker, no API of its own',
    ]);
    expect(el.querySelector('table.routes tbody tr')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('ANY /api/v1/orders/ authenticated');
    expect(Array.from(el.querySelectorAll('table.queues tbody tr')).map((r) => r.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      "ordering-catalog-events Ordering's price projection 2 — Common.Contracts.Catalog.V1:PriceChanged",
      "[error] ordering-catalog-events_error Ordering's price projection 1 —",
    ]);
    expect(el.querySelector('table.queues tr.error-queue')).not.toBeNull();
  });

  it('lists a disagreement and resolves none', () => {
    const el: HTMLElement = render().nativeElement;

    expect(el.querySelector('section.disagreements li')?.textContent).toContain('mystery-queue is on the broker');
  });

  it('says the broker did not answer and still shows the routes', () => {
    host.platformMap.mockReturnValue(of({ ...view, brokerReachable: false, error: 'docker is not running', disagreements: [] }));
    const el: HTMLElement = render().nativeElement;

    expect(el.querySelector('[role="alert"]')?.textContent).toContain('The broker did not answer: docker is not running');
    expect(el.querySelector('table.routes')).not.toBeNull();
  });

  it('says the host did not answer when the read fails, and not Reading the platform', () => {
    host.platformMap.mockReturnValue(throwError(() => new Error('connection refused')));
    const el: HTMLElement = render().nativeElement;

    expect(el.querySelector('[role="alert"]')?.textContent).toContain('connection refused');
    expect(el.textContent).not.toContain('Reading the platform');
  });

  it('reads a rate off two reads of the same queue', () => {
    vi.setSystemTime(new Date('2026-10-20T10:00:00Z'));
    const fixture = render();
    const feed = TestBed.inject(MapFeed);
    expect(feed.rateOf('ordering-catalog-events')).toBe('—');

    vi.setSystemTime(new Date('2026-10-20T10:00:02Z'));
    host.platformMap.mockReturnValue(
      of({
        ...view,
        services: view.services.map((s) =>
          s.name === 'ordering' ? { ...s, queues: s.queues.map((q) => (q.parked ? q : { ...q, messages: 6 })) } : s,
        ),
      }),
    );
    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(feed.rateOf('ordering-catalog-events')).toBe('+2.0/s');
    expect(feed.rateOf('ordering-catalog-events_error')).toBe('0/s');
    expect((fixture.nativeElement.querySelector('table.queues td.rate') as HTMLElement).textContent).toBe('+2.0/s');
  });

  it('reads once when it appears and polls nothing while its auto-refresh is off', async () => {
    render();
    expect(host.platformMap).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(60_000);
    expect(host.platformMap).toHaveBeenCalledTimes(1);
  });

  it('polls every 5 seconds while its own auto-refresh is on, and stops when it is destroyed', async () => {
    const fixture = render({ autoRefresh: true });
    await vi.advanceTimersByTimeAsync(0);
    const first = host.platformMap.mock.calls.length;

    await vi.advanceTimersByTimeAsync(10_000);
    expect(host.platformMap.mock.calls.length).toBe(first + 2);

    fixture.destroy();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(host.platformMap.mock.calls.length).toBe(first + 2);
  });

  it('refresh is disabled while a read is out', () => {
    const fixture = render();
    const button = fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement;
    expect(button.disabled).toBe(false);

    TestBed.inject(MapFeed).map.refreshing.set(true);
    fixture.detectChanges();

    expect(button.disabled).toBe(true);
  });
});
