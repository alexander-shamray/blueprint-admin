import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { TraceView } from '../../../core/host/host-types';
import { WIDGET_CONTEXT } from '../../../workspace/widget-context';
import { TraceFollow } from '../trace-follow';
import { TraceWidget } from './trace-widget';

function viewOf(id: string): TraceView {
  return {
    correlationId: id,
    window: '15m',
    reachable: true,
    error: null,
    traceIds: ['t1'],
    tracesTruncated: false,
    warning: null,
    events: [
      {
        at: '2026-09-16T09:43:00.000Z',
        source: 'loki',
        service: 'Gateway.Api',
        kind: 'Log',
        summary: `a line for ${id}`,
        traceId: 't1',
        link: null,
      },
    ],
  };
}

describe('TraceWidget', () => {
  let host: { trace: ReturnType<typeof vi.fn> };

  function configure(extra: unknown[] = [], routeParams: Record<string, string> = {}) {
    const paramMap = new BehaviorSubject(convertToParamMap(routeParams));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HostClient, useValue: host },
        // An operator's page: a route that carries a page id and no correlation id.
        { provide: ActivatedRoute, useValue: { paramMap, snapshot: { paramMap: paramMap.value, data: {} } } },
        ...(extra as []),
      ],
    });
  }

  function render(config: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(TraceWidget);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    host = { trace: vi.fn((id: string) => of(viewOf(id))) };
  });

  it('starts at the correlation id and window it is configured with', () => {
    configure();
    const fixture = render({ correlationId: 'configured-1', window: '6h' });

    expect(host.trace).toHaveBeenCalledWith('configured-1', '6h');
    expect(fixture.nativeElement.querySelector('p.summary')?.textContent).toContain('for configured-1');
  });

  it('reads nothing on appearing when it has no id to start at', () => {
    configure();
    render();

    expect(host.trace).not.toHaveBeenCalled();
  });

  it('loads the id typed into it on Trace, itself, when it is not on the built-in Trace page', () => {
    configure();
    const fixture = render();
    fixture.componentInstance.correlationId.set('  typed-1  ');

    fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(host.trace).toHaveBeenCalledWith('typed-1', '15m');
    expect(fixture.nativeElement.querySelector('table.timeline')).not.toBeNull();
  });

  describe('following the last Trace link', () => {
    it('is re-pointed by a link made after it appeared, when its config says follow', () => {
      configure();
      const fixture = render({ follow: true });
      expect(TestBed.inject(TraceFollow).followers()).toBe(1);

      TestBed.inject(TraceFollow).point('linked-1');
      fixture.detectChanges();

      expect(host.trace).toHaveBeenCalledWith('linked-1', '15m');
      expect(fixture.componentInstance.correlationId()).toBe('linked-1');
      expect(fixture.nativeElement.querySelector('p.summary')?.textContent).toContain('for linked-1');
    });

    it('ignores a link when its config does not say follow, and is not counted as a follower', () => {
      configure();
      const fixture = render({ follow: false });

      TestBed.inject(TraceFollow).point('linked-2');
      fixture.detectChanges();

      expect(host.trace).not.toHaveBeenCalled();
      expect(TestBed.inject(TraceFollow).followers()).toBe(0);
    });

    it('stops being a follower when its config stops saying follow, and when it is destroyed', () => {
      configure();
      const fixture = render({ follow: true });
      const follow = TestBed.inject(TraceFollow);
      expect(follow.followers()).toBe(1);

      fixture.componentRef.setInput('config', { follow: false });
      fixture.detectChanges();
      expect(follow.followers()).toBe(0);

      fixture.componentRef.setInput('config', { follow: true });
      fixture.detectChanges();
      expect(follow.followers()).toBe(1);
      fixture.destroy();
      expect(follow.followers()).toBe(0);
    });

    it('is not told of a link made before it appeared', () => {
      configure();
      TestBed.inject(TraceFollow).point('old-link');

      render({ follow: true });

      expect(host.trace).not.toHaveBeenCalled();
    });
  });

  it('traces the id it last traced again when it is re-created (session state, §4.2)', () => {
    configure([{ provide: WIDGET_CONTEXT, useValue: { instanceId: 'w1', pageId: 'p1' } }]);
    const first = render({ correlationId: 'a' });
    first.componentInstance.correlationId.set('b');
    first.componentInstance.submit();
    first.destroy();
    host.trace.mockClear();

    const again = render({ correlationId: 'a' });

    expect(host.trace).toHaveBeenCalledWith('b', '15m');
    expect(again.nativeElement.querySelector('p.summary')?.textContent).toContain('for b');
  });

  it('traces an id the settings editor configures later, and not one it leaves as it was', () => {
    configure();
    const fixture = render({ correlationId: 'first' });
    host.trace.mockClear();

    fixture.componentRef.setInput('config', { correlationId: 'first', window: '1h' });
    fixture.detectChanges();
    expect(host.trace).not.toHaveBeenCalledWith('first', expect.anything());

    fixture.componentRef.setInput('config', { correlationId: 'second' });
    fixture.detectChanges();
    expect(host.trace).toHaveBeenCalledWith('second', '15m');
  });

  describe('on the built-in Trace page', () => {
    function configureBuiltIn(id: string) {
      const paramMap = new BehaviorSubject(convertToParamMap({ correlationId: id }));
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: HostClient, useValue: host },
          {
            provide: ActivatedRoute,
            useValue: { paramMap, snapshot: { paramMap: paramMap.value, data: { builtIn: 'trace' } } },
          },
        ],
      });
      return TestBed.inject(Router);
    }

    it('a link that re-points it navigates to the id, so the address says what is on screen', () => {
      const navigate = vi.spyOn(configureBuiltIn('old-id'), 'navigate').mockResolvedValue(true);
      const fixture = render({ follow: true });
      host.trace.mockClear();

      TestBed.inject(TraceFollow).point('new-id');
      fixture.detectChanges();

      expect(navigate).toHaveBeenCalledWith(['/trace', 'new-id']);
      expect(host.trace).not.toHaveBeenCalled();
    });

    it('Trace on the id already in the address loads it, since navigating to where it already is does nothing', () => {
      const navigate = vi.spyOn(configureBuiltIn('same-id'), 'navigate').mockResolvedValue(true);
      const fixture = render();
      host.trace.mockClear();

      fixture.componentInstance.correlationId.set('same-id');
      fixture.componentInstance.submit();

      expect(navigate).not.toHaveBeenCalled();
      expect(host.trace).toHaveBeenCalledWith('same-id', '15m');
    });
  });

  it('keeps the window the operator chose for the tab, over the configured one, when it is re-created', () => {
    configure([{ provide: WIDGET_CONTEXT, useValue: { instanceId: 'w1', pageId: 'p1' } }]);
    const first = render({ window: '1h' });
    expect(first.componentInstance.shownWindow()).toBe('1h');
    first.componentInstance.window.set('24h');
    first.destroy();

    const again = render({ window: '1h' });

    expect(again.componentInstance.shownWindow()).toBe('24h');
  });

  it('takes a window the config comes to name later over the one the operator chose', () => {
    configure([{ provide: WIDGET_CONTEXT, useValue: { instanceId: 'w1', pageId: 'p1' } }]);
    const fixture = render({ correlationId: 'a', window: '1h' });
    fixture.componentInstance.window.set('24h');

    fixture.componentRef.setInput('config', { correlationId: 'a', window: '6h' });
    fixture.detectChanges();

    expect(fixture.componentInstance.shownWindow()).toBe('6h');
  });

  it('takes the id from the built-in Trace route over any configured one', () => {
    configure([], { correlationId: 'from-route' });
    render({ correlationId: 'configured' });

    expect(host.trace).toHaveBeenCalledTimes(1);
    expect(host.trace).toHaveBeenCalledWith('from-route', '15m');
  });
});
