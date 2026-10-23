import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { QueuesView } from '../../../core/host/host-types';
import { BrokerFeed } from '../broker-feed';
import { brokerHost, queues, rows } from '../broker-testing';
import { QueuesWidget } from './queues-widget';

describe('QueuesWidget', () => {
  let host: ReturnType<typeof brokerHost>;

  beforeEach(() => {
    vi.useFakeTimers();
    host = brokerHost();
    TestBed.configureTestingModule({ providers: [{ provide: HostClient, useValue: host }] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function render(config: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(QueuesWidget);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    return fixture;
  }

  it('lists queues with depth, marks error queues in text, and shows the drained indicator', () => {
    const fixture = render();

    expect(rows(fixture, 'queues')).toEqual([
      "ordering-catalog-events 2 Ordering's price projection",
      "[error] ordering-catalog-events_error 1 Ordering's price projection error-queue runbook",
      "[skipped] bff-order-events_skipped 1 the BFF's order projection skipped-queue runbook",
      "[skipped] payments-events_skipped 0 Payments' order events",
      'something-else 0',
      "bff-order-events 0 the BFF's order projection",
    ]);
    expect(fixture.nativeElement.querySelector('table.queues tr.error-queue')).not.toBeNull();
    expect(fixture.nativeElement.querySelectorAll('table.queues tr.skipped-queue').length).toBe(2);
    expect(fixture.nativeElement.querySelector('app-drained-indicator')?.textContent).toContain('[waiting]');
  });

  it('links the runbook of a parked queue that holds a message, and of an empty one not', () => {
    const fixture = render();
    const links = Array.from(fixture.nativeElement.querySelectorAll('table.queues td.runbook a')) as HTMLAnchorElement[];

    expect(links.map((a) => a.getAttribute('href'))).toEqual([
      'https://example.test/runbooks/error-queue.md',
      'https://example.test/runbooks/skipped-queue.md',
    ]);
    expect(links.every((a) => a.target === '_blank' && a.rel === 'noopener noreferrer')).toBe(true);
  });

  it('filters by name and shows parked queues only, as its config says', () => {
    expect(rows(render({ filter: 'BFF' }), 'queues').map((r) => r.split(' ')[0] === '[skipped]')).toEqual([true, false]);

    const parked = render({ parkedOnly: true });
    expect(rows(parked, 'queues').length).toBe(3);
  });

  it('reads once when it appears and polls nothing while its auto-refresh is off', async () => {
    render();
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(60_000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
  });

  it('polls every 5 seconds while its own auto-refresh is on, and stops when it is switched off', async () => {
    const fixture = render({ autoRefresh: true });
    await vi.advanceTimersByTimeAsync(0);
    const first = host.brokerQueues.mock.calls.length;

    await vi.advanceTimersByTimeAsync(10_000);
    expect(host.brokerQueues.mock.calls.length).toBe(first + 2);

    fixture.componentRef.setInput('config', { autoRefresh: false });
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(host.brokerQueues.mock.calls.length).toBe(first + 2);
  });

  it('stops polling when it is destroyed', async () => {
    const fixture = render({ autoRefresh: true });
    await vi.advanceTimersByTimeAsync(0);
    const first = host.brokerQueues.mock.calls.length;

    fixture.destroy();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(host.brokerQueues.mock.calls.length).toBe(first);
  });

  it('refresh reads again, and is disabled while a read is out', () => {
    const inFlight = new Subject<QueuesView>();
    const fixture = render();
    host.brokerQueues.mockReturnValueOnce(inFlight);
    const button = fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement;

    button.click();
    fixture.detectChanges();
    expect(button.disabled).toBe(true);
    button.click();
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);

    inFlight.next(queues);
    inFlight.complete();
    fixture.detectChanges();
    expect(button.disabled).toBe(false);
  });

  it('a failed read shows the host error and keeps the last queues on screen', () => {
    const fixture = render();
    host.brokerQueues.mockReturnValue(throwError(() => ({ error: { detail: 'The host is gone' } })));

    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('p.error')?.textContent).toContain('The host is gone');
    expect(rows(fixture, 'queues').length).toBe(queues.queues.length);
  });

  it('does not show Loading behind a first-load error, and shows the broker error when it did not answer', () => {
    host.brokerQueues.mockReturnValue(throwError(() => new Error('down')));
    const failed = render();
    expect(failed.nativeElement.textContent).not.toContain('Loading');

    TestBed.inject(BrokerFeed).queues.value.set({ ...queues, reachable: false, error: 'rabbitmqctl did not answer' });
    host.brokerQueues.mockReturnValue(of({ ...queues, reachable: false, error: 'rabbitmqctl did not answer' }));
    const unreachable = render();
    expect(unreachable.nativeElement.querySelector('p.error')?.textContent).toContain('rabbitmqctl did not answer');
    expect(unreachable.nativeElement.querySelector('app-drained-indicator')).toBeNull();
  });

  describe('Open', () => {
    @Component({ selector: 'app-bench', template: '' })
    class Bench {
      constructor() {
        TestBed.inject(BrokerFeed).workbenches.update((n) => n + 1);
      }
    }

    it('is offered only for a parked queue that holds a message, and only while a workbench is on the page', () => {
      const without = render();
      expect(without.nativeElement.querySelector('button.open-dead-letters')).toBeNull();

      TestBed.createComponent(Bench);
      const fixture = render();
      const buttons = Array.from(fixture.nativeElement.querySelectorAll('button.open-dead-letters')) as HTMLButtonElement[];

      expect(buttons.map((b) => b.getAttribute('aria-label'))).toEqual([
        'Open the messages on ordering-catalog-events_error',
        'Open the messages on bff-order-events_skipped',
      ]);
    });

    it('is not offered when the host did not give the plan', () => {
      host.deadLetterPlan.mockReturnValue(throwError(() => new Error('down')));
      TestBed.createComponent(Bench);
      const fixture = render();

      expect(fixture.nativeElement.querySelector('button.open-dead-letters')).toBeNull();
    });

    it('asks the workbench to open the queue', () => {
      TestBed.createComponent(Bench);
      const fixture = render();

      (fixture.nativeElement.querySelector('button.open-dead-letters') as HTMLButtonElement).click();

      expect(TestBed.inject(BrokerFeed).openRequest()).toEqual({ queue: 'ordering-catalog-events_error', n: 1 });
    });
  });
});
