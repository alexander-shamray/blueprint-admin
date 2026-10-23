import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { QueuesView } from '../../../core/host/host-types';
import { JobEvent, SseClient } from '../../../core/host/sse-client';
import { WIDGET_CONTEXT } from '../../../workspace/widget-context';
import { brokerHost, plan, queues } from '../broker-testing';
import { BffProjectionWidget } from './bff-projection-widget';

describe('BffProjectionWidget', () => {
  let host: ReturnType<typeof brokerHost>;

  beforeEach(() => {
    host = brokerHost();
    TestBed.configureTestingModule({
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => of() } },
      ],
    });
  });

  function render() {
    const fixture = TestBed.createComponent(BffProjectionWidget);
    fixture.detectChanges();
    return fixture;
  }

  function withBffDepth(messages: number): QueuesView {
    return { ...queues, queues: queues.queues.map((q) => (q.name === 'bff-order-events' ? { ...q, messages } : q)) };
  }

  function type(fixture: { nativeElement: HTMLElement; whenStable: () => Promise<unknown>; detectChanges: () => void }, value: string) {
    const input = fixture.nativeElement.querySelector('.rebuild-box input') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    return fixture.whenStable().then(() => fixture.detectChanges());
  }

  it("states the projection tool's limits and the queue's depth before anything runs", () => {
    const fixture = render();
    const panel = fixture.nativeElement.querySelector('section.projection') as HTMLElement;

    expect(Array.from(panel.querySelectorAll('ul.limits li')).map((li) => li.textContent?.trim())).toEqual(plan.limits);
    expect(panel.querySelector('.depth')?.textContent?.trim()).toBe('[drained] bff-order-events is empty.');
    expect(fixture.nativeElement.querySelector('app-output-pane')).toBeNull();
  });

  it('says how many messages the projection queue holds while it is not empty', () => {
    host.brokerQueues.mockReturnValue(of(withBffDepth(3)));
    const fixture = render();

    expect(fixture.nativeElement.querySelector('.depth')?.textContent?.trim()).toBe('[waiting] bff-order-events holds 3 messages.');
  });

  it('repair starts the job without a confirmation, shows its output and reads the queues again', () => {
    const fixture = render();
    host.brokerQueues.mockClear();

    (fixture.nativeElement.querySelector('button.repair') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(host.projectionRepair).toHaveBeenCalledTimes(1);
    expect(host.projectionRebuild).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('app-output-pane')).not.toBeNull();
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
  });

  it("reads the depth line again when the run's job exits, so it does not keep the queue as the run found it", () => {
    const events = new Subject<JobEvent>();
    TestBed.overrideProvider(SseClient, { useValue: { follow: () => events } });
    host.brokerQueues.mockReturnValue(of(withBffDepth(3)));
    const fixture = render();
    (fixture.nativeElement.querySelector('button.repair') as HTMLButtonElement).click();
    fixture.detectChanges();
    host.brokerQueues.mockClear();
    host.brokerQueues.mockReturnValue(of(withBffDepth(0)));

    events.next({ kind: 'exited', exitCode: 0 });
    fixture.detectChanges();

    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelector('.depth')?.textContent?.trim()).toBe('[drained] bff-order-events is empty.');
  });

  it('polls the queues while its run goes, so the depth line follows it, and stops when the run ends', async () => {
    vi.useFakeTimers();
    try {
      const events = new Subject<JobEvent>();
      TestBed.overrideProvider(SseClient, { useValue: { follow: () => events } });
      const fixture = render();
      (fixture.nativeElement.querySelector('button.repair') as HTMLButtonElement).click();
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      host.brokerQueues.mockClear();

      await vi.advanceTimersByTimeAsync(10_000);
      expect(host.brokerQueues.mock.calls.length).toBe(2);

      events.next({ kind: 'exited', exitCode: 0 });
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      host.brokerQueues.mockClear();
      await vi.advanceTimersByTimeAsync(60_000);
      expect(host.brokerQueues).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it('a start while a read is still out reads again once that read lands, since it may have read before the start', () => {
    const fixture = render();
    const inFlight = new Subject<QueuesView>();
    host.brokerQueues.mockClear();
    host.brokerQueues.mockReturnValueOnce(inFlight);
    // A read out from elsewhere, another widget's Refresh, when the operator presses Repair.
    fixture.componentInstance.feed.queues.refresh();

    (fixture.nativeElement.querySelector('button.repair') as HTMLButtonElement).click();
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
    inFlight.next(queues);
    inFlight.complete();
    fixture.detectChanges();

    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
  });

  it('rebuild is disabled until the confirmation is typed exactly, then sends it and clears the box', async () => {
    const fixture = render();
    const button = fixture.nativeElement.querySelector('button.rebuild') as HTMLButtonElement;
    expect(button.disabled).toBe(true);

    await type(fixture, 'bff-replay --rese');
    expect(button.disabled).toBe(true);

    await type(fixture, 'bff-replay --reset');
    expect(button.disabled).toBe(false);

    button.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.projectionRebuild).toHaveBeenCalledWith('bff-replay --reset');
    expect(fixture.nativeElement.querySelector('app-output-pane')).not.toBeNull();
    expect(button.disabled).toBe(true);
  });

  it('a refused rebuild keeps the typed confirmation, so a retry is not retyped', async () => {
    host.projectionRebuild.mockReturnValue(throwError(() => ({ error: { detail: 'A repair is running' } })));
    const fixture = render();
    const button = fixture.nativeElement.querySelector('button.rebuild') as HTMLButtonElement;
    await type(fixture, 'bff-replay --reset');

    button.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('p.error')?.textContent).toContain('A repair is running');
    expect(button.disabled).toBe(false);
  });

  it("a refused start shows the host's reason and no output pane", () => {
    host.projectionRepair.mockReturnValue(throwError(() => ({ error: { detail: 'Confirmation required' } })));
    const fixture = render();

    (fixture.nativeElement.querySelector('button.repair') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('p.error')?.textContent).toContain('Confirmation required');
    expect(fixture.nativeElement.querySelector('app-output-pane')).toBeNull();
  });

  it('draws no actions when the host does not answer the plan', () => {
    host.projectionPlan.mockReturnValue(throwError(() => new Error('down')));
    const fixture = render();

    expect(fixture.nativeElement.querySelector('section.projection')).toBeNull();
    expect(fixture.nativeElement.querySelector('button.rebuild')).toBeNull();
  });

  it('is re-created with the confirmation empty, and its job still shown', async () => {
    TestBed.configureTestingModule({ providers: [{ provide: WIDGET_CONTEXT, useValue: { instanceId: 'w1', pageId: 'p1' } }] });
    const first = render();
    await type(first, 'bff-replay --reset');
    (first.nativeElement.querySelector('button.repair') as HTMLButtonElement).click();
    first.detectChanges();
    first.destroy();

    const again = render();

    expect((again.nativeElement.querySelector('.rebuild-box input') as HTMLInputElement).value).toBe('');
    expect((again.nativeElement.querySelector('button.rebuild') as HTMLButtonElement).disabled).toBe(true);
    expect(again.nativeElement.querySelector('app-output-pane')).not.toBeNull();
  });
});
