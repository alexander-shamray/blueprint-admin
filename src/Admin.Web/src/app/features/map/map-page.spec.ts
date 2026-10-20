import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { PlatformMapView } from '../../core/host/host-types';
import { MapPage } from './map-page';

const view: PlatformMapView = {
  brokerReachable: true,
  error: null,
  services: [
    { name: 'gateway', url: 'http://localhost:5000', routes: [], queues: [] },
    {
      name: 'ordering',
      url: 'http://localhost:5101',
      routes: [{ method: null, prefix: '/api/v1/orders/', policy: 'authenticated' }],
      queues: [
        {
          name: 'ordering-catalog-events',
          consumer: "Ordering's price projection",
          messages: 2,
          parked: null,
          bindings: [{ source: 'Common.Contracts.Catalog.V1:PriceChanged', routingKey: '' }],
        },
        { name: 'ordering-catalog-events_error', consumer: "Ordering's price projection", messages: 1, parked: 'error', bindings: [] },
      ],
    },
    { name: 'shipping', url: null, routes: [], queues: [] },
  ],
  disagreements: ['mystery-queue is on the broker but in no service\'s queue table.'],
};

describe('MapPage', () => {
  let host: { platformMap: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    host = { platformMap: vi.fn(() => of(view)) };
    TestBed.configureTestingModule({ imports: [MapPage], providers: [{ provide: HostClient, useValue: host }] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function render() {
    const fixture = TestBed.createComponent(MapPage);
    fixture.detectChanges();
    return fixture;
  }

  it('draws a section per service with its routes and queues, and names a worker as having no API', () => {
    const fixture = render();
    const el: HTMLElement = fixture.nativeElement;

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

  it('says the host did not answer when the read fails', () => {
    host.platformMap.mockReturnValue(throwError(() => new Error('connection refused')));
    const el: HTMLElement = render().nativeElement;

    expect(el.querySelector('[role="alert"]')?.textContent).toContain('connection refused');
  });

  it('reads a rate off two reads of the same queue', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-20T10:00:00Z'));
    const fixture = render();
    expect(fixture.componentInstance.rateOf('ordering-catalog-events')).toBe('—');

    vi.setSystemTime(new Date('2026-10-20T10:00:02Z'));
    host.platformMap.mockReturnValue(
      of({
        ...view,
        services: view.services.map((s) =>
          s.name === 'ordering' ? { ...s, queues: s.queues.map((q) => (q.parked ? q : { ...q, messages: 6 })) } : s,
        ),
      }),
    );
    fixture.componentInstance.refresh();
    fixture.detectChanges();

    expect(fixture.componentInstance.rateOf('ordering-catalog-events')).toBe('+2.0/s');
    expect(fixture.componentInstance.rateOf('ordering-catalog-events_error')).toBe('0/s');
  });
});
