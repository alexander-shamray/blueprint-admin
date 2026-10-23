import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { TraceEvent, TraceView } from '../../../core/host/host-types';
import { TraceFollow } from '../../../features/trace/trace-follow';
import { WIDGET_CONTEXT } from '../../widget-context';
import { DIAGRAM_RENDERER } from '../note/mermaid-diagram';
import { SequenceWidget } from './sequence-widget';

function event(partial: Partial<TraceEvent>): TraceEvent {
  return {
    at: '2026-09-16T09:43:00.000Z',
    source: 'tempo',
    service: 'Gateway.Api',
    kind: 'HttpIn',
    summary: 'GET /x (5 ms)',
    traceId: 't1',
    link: null,
    ...partial,
  };
}

function viewOf(
  id: string,
  events: TraceEvent[] = [
    event({ link: `http://grafana/explore?${id}` }),
    event({ kind: 'Publish', service: 'Ordering.Api', summary: 'send (1 ms)' }),
  ],
): TraceView {
  return {
    correlationId: id,
    window: '15m',
    reachable: true,
    error: null,
    traceIds: ['t1'],
    tracesTruncated: false,
    warning: null,
    events,
  };
}

describe('SequenceWidget', () => {
  let host: { trace: ReturnType<typeof vi.fn> };
  let drawn: string[];

  beforeEach(() => {
    host = { trace: vi.fn((id: string) => of(viewOf(id))) };
    drawn = [];
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HostClient, useValue: host },
        {
          provide: DIAGRAM_RENDERER,
          useValue: (source: string) => (drawn.push(source), Promise.resolve('<svg><text>drawn</text></svg>')),
        },
        { provide: WIDGET_CONTEXT, useValue: { instanceId: 'w1', pageId: 'p1' } },
      ],
    });
  });

  function render(config: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(SequenceWidget);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    return fixture;
  }

  it('reads nothing and draws nothing until it has an id', () => {
    const fixture = render();

    expect(host.trace).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('Enter a correlation id');
  });

  it('draws the configured id: the sequence source is handed to the diagram, and every arrow is listed with its Explore link', async () => {
    const fixture = render({ correlationId: 'c1', window: '6h' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.trace).toHaveBeenCalledWith('c1', '6h');
    expect(drawn[0]).toContain('sequenceDiagram');
    expect(drawn[0]).toContain('C->>S0: GET /x (5 ms)');
    const items = fixture.nativeElement.querySelectorAll('ol.arrows li');
    expect(items.length).toBe(2);
    expect(items[0].querySelector('a.explore').getAttribute('href')).toBe('http://grafana/explore?c1');
    expect(items[1].querySelector('a.explore')).toBeNull();
  });

  it('says there is nothing to draw, rather than drawing an empty diagram', async () => {
    host.trace.mockReturnValue(of(viewOf('c2', [event({ kind: 'Log', source: 'loki' })])));
    const fixture = render({ correlationId: 'c2' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(drawn).toEqual([]);
    expect(fixture.nativeElement.querySelector('p.empty').textContent).toContain('holds no hop to draw');
  });

  it('shows an unreachable Grafana as an error and keeps no diagram of it', () => {
    host.trace.mockReturnValue(of({ ...viewOf('c3'), reachable: false, error: 'Grafana did not answer.' }));
    const fixture = render({ correlationId: 'c3' });

    expect(fixture.nativeElement.querySelector('p.error').textContent).toContain('Grafana did not answer.');
    expect(fixture.componentInstance.sequence()).toBeNull();
  });

  it('is not polled: a second read happens only on Reload, and says what moved', () => {
    const fixture = render({ correlationId: 'c1' });
    expect(host.trace).toHaveBeenCalledTimes(1);

    host.trace.mockReturnValue(
      of(
        viewOf('c1', [
          ...viewOf('c1').events,
          event({ kind: 'Consume', service: 'Inventory.Api', summary: 'process (2 ms)' }),
        ]),
      ),
    );
    fixture.componentInstance.reload();
    fixture.detectChanges();

    expect(host.trace).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.querySelector('p.changes').textContent).toContain(
      '1 new, 0 no longer in the timeline',
    );
  });

  describe('following the last Trace link', () => {
    it('is re-drawn by a link made after it appeared, when its config says follow', () => {
      const fixture = render({ follow: true });
      expect(TestBed.inject(TraceFollow).followers()).toBe(1);

      TestBed.inject(TraceFollow).point('linked-1');
      fixture.detectChanges();

      expect(host.trace).toHaveBeenCalledWith('linked-1', '15m');
      expect(fixture.componentInstance.correlationId()).toBe('linked-1');
    });

    it('ignores a link when its config does not say follow, and is not counted as a follower', () => {
      const fixture = render({ follow: false });

      TestBed.inject(TraceFollow).point('linked-2');
      fixture.detectChanges();

      expect(host.trace).not.toHaveBeenCalled();
      expect(TestBed.inject(TraceFollow).followers()).toBe(0);
    });
  });

  it('draws the id it last drew again when it is re-created, and the window the operator chose over the configured one', () => {
    const first = render({ correlationId: 'a', window: '1h' });
    first.componentInstance.correlationId.set('b');
    first.componentInstance.window.set('24h');
    first.componentInstance.submit();
    first.destroy();
    host.trace.mockClear();

    render({ correlationId: 'a', window: '1h' });

    expect(host.trace).toHaveBeenCalledWith('b', '24h');
  });

  it('draws an id the settings editor configures later, and not one it leaves as it was', () => {
    const fixture = render({ correlationId: 'first' });
    host.trace.mockClear();

    fixture.componentRef.setInput('config', { correlationId: 'first', window: '1h' });
    fixture.detectChanges();
    expect(host.trace).not.toHaveBeenCalled();

    fixture.componentRef.setInput('config', { correlationId: 'second' });
    fixture.detectChanges();
    expect(host.trace).toHaveBeenCalledWith('second', '15m');
  });
});
