import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ExchangesView, JobSummary, PermissionsView, ProjectionPlan, QueuesView } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';
import { BrokerPage } from './broker-page';

const queues: QueuesView = {
  reachable: true,
  error: null,
  queues: [
    { name: 'ordering-catalog-events', messages: 2, isErrorQueue: false, consumer: "Ordering's price projection", parked: null, runbook: null },
    {
      name: 'ordering-catalog-events_error',
      messages: 1,
      isErrorQueue: true,
      consumer: "Ordering's price projection",
      parked: 'error',
      runbook: 'https://example.test/runbooks/error-queue.md',
    },
    {
      name: 'bff-order-events_skipped',
      messages: 1,
      isErrorQueue: false,
      consumer: "the BFF's order projection",
      parked: 'skipped',
      runbook: 'https://example.test/runbooks/skipped-queue.md',
    },
    {
      name: 'payments-events_skipped',
      messages: 0,
      isErrorQueue: false,
      consumer: "Payments' order events",
      parked: 'skipped',
      runbook: 'https://example.test/runbooks/skipped-queue.md',
    },
    { name: 'something-else', messages: 0, isErrorQueue: false, consumer: null, parked: null, runbook: null },
    { name: 'bff-order-events', messages: 0, isErrorQueue: false, consumer: "the BFF's order projection", parked: null, runbook: null },
  ],
  projection: { queue: 'ordering-catalog-events', found: true, messages: 2, drained: false },
};

const plan: ProjectionPlan = {
  queue: 'bff-order-events',
  rebuildConfirm: 'bff-replay --reset',
  limits: ['A replay reaches back one outbox window and no further.', 'A rebuild deletes the projection first.'],
};

const job: JobSummary = { id: 'job-7', commandLine: 'bff-replay', state: 'Running', exitCode: null, startedAt: '2026-10-15T00:00:00Z' };

const exchanges: ExchangesView = {
  reachable: true,
  error: null,
  exchanges: [{ name: '', type: 'direct' }, { name: 'ordering-fulfilment-saga_delay', type: 'x-delayed-message' }],
};

const permissions: PermissionsView = {
  reachable: true,
  error: null,
  permissions: [{ user: 'catalog-svc', configure: '^(MassTransit:)', write: '^(MassTransit:)', read: '^(MassTransit:)' }],
};

function rows(fixture: { nativeElement: HTMLElement }, table: string): string[] {
  return (Array.from(fixture.nativeElement.querySelectorAll(`table.${table} tbody tr`)) as HTMLElement[])
    .map((r) => r.textContent?.replace(/\s+/g, ' ').trim() ?? '');
}

describe('BrokerPage', () => {
  let host: {
    brokerQueues: ReturnType<typeof vi.fn>;
    brokerExchanges: ReturnType<typeof vi.fn>;
    brokerPermissions: ReturnType<typeof vi.fn>;
    projectionPlan: ReturnType<typeof vi.fn>;
    projectionRepair: ReturnType<typeof vi.fn>;
    projectionRebuild: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    host = {
      brokerQueues: vi.fn(() => of(queues)),
      brokerExchanges: vi.fn(() => of(exchanges)),
      brokerPermissions: vi.fn(() => of(permissions)),
      projectionPlan: vi.fn(() => of(plan)),
      projectionRepair: vi.fn(() => of(job)),
      projectionRebuild: vi.fn(() => of(job)),
    };
    TestBed.configureTestingModule({
      imports: [BrokerPage],
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => of() } },
      ],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function render() {
    const fixture = TestBed.createComponent(BrokerPage);
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

  it("states the projection tool's limits and the queue's depth before anything runs", () => {
    const fixture = render();
    const panel = fixture.nativeElement.querySelector('section.projection') as HTMLElement;

    expect(panel.querySelector('h2')?.textContent).toBe('BFF order projection');
    expect(Array.from(panel.querySelectorAll('ul.limits li')).map((li) => li.textContent?.trim())).toEqual(plan.limits);
    expect(panel.querySelector('.depth')?.textContent?.trim()).toBe('[drained] bff-order-events is empty.');
    expect(fixture.nativeElement.querySelector('app-output-pane')).toBeNull();
  });

  it('says how many messages the projection queue holds while it is not empty', () => {
    host.brokerQueues.mockReturnValue(
      of({ ...queues, queues: queues.queues.map((q) => (q.name === 'bff-order-events' ? { ...q, messages: 3 } : q)) }),
    );
    const fixture = render();

    expect(fixture.nativeElement.querySelector('section.projection .depth')?.textContent?.trim()).toBe('[waiting] bff-order-events holds 3 messages.');
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

  it('rebuild is disabled until the confirmation is typed exactly, then sends it and clears the box', async () => {
    const fixture = render();
    const button = fixture.nativeElement.querySelector('button.rebuild') as HTMLButtonElement;
    const input = fixture.nativeElement.querySelector('.rebuild-box input') as HTMLInputElement;

    expect(button.disabled).toBe(true);

    input.value = 'bff-replay --rese';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(button.disabled).toBe(true);

    input.value = 'bff-replay --reset';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(button.disabled).toBe(false);

    button.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.projectionRebuild).toHaveBeenCalledWith('bff-replay --reset');
    expect(fixture.nativeElement.querySelector('app-output-pane')).not.toBeNull();
    expect(button.disabled).toBe(true);
  });

  it("a refused start shows the host's reason and no output pane", () => {
    host.projectionRepair.mockReturnValue(throwError(() => ({ error: { detail: 'Confirmation required' } })));
    const fixture = render();

    (fixture.nativeElement.querySelector('button.repair') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('section.projection p.error')?.textContent).toContain('Confirmation required');
    expect(fixture.nativeElement.querySelector('app-output-pane')).toBeNull();
  });

  it('draws no projection panel when the host does not answer the plan', () => {
    host.projectionPlan.mockReturnValue(throwError(() => new Error('down')));
    const fixture = render();

    expect(fixture.nativeElement.querySelector('section.projection')).toBeNull();
    expect(rows(fixture, 'queues').length).toBe(queues.queues.length);
  });

  it('lists exchanges, naming the default exchange, and permissions per user', () => {
    const fixture = render();

    expect(rows(fixture, 'exchanges')).toEqual(['(default) direct', 'ordering-fulfilment-saga_delay x-delayed-message']);
    expect(rows(fixture, 'permissions')).toEqual(['catalog-svc ^(MassTransit:) ^(MassTransit:) ^(MassTransit:)']);
  });

  it('does not show Loading behind a first-load error', () => {
    host.brokerQueues.mockReturnValue(throwError(() => ({ error: { title: 'Server error', detail: 'boom' } })));

    const fixture = render();

    expect(fixture.nativeElement.textContent).not.toContain('Loading');
    expect(fixture.nativeElement.textContent).toContain('boom');
  });

  it('shows the broker error when rabbitmqctl did not answer, and no indicator', () => {
    host.brokerQueues.mockReturnValue(of({ ...queues, reachable: false, error: 'service "rabbitmq" is not running', queues: [] }));

    const fixture = render();

    const message = fixture.nativeElement.querySelector('p.error');
    expect(message?.textContent).toContain('The broker did not answer: service "rabbitmq" is not running');
    expect(message?.getAttribute('role')).toBe('alert');
    expect(fixture.nativeElement.querySelector('app-drained-indicator')).toBeNull();
  });

  it('marks the exchanges broker-unreachable message as an alert', () => {
    host.brokerExchanges.mockReturnValue(of({ ...exchanges, reachable: false, error: 'service "rabbitmq" is not running', exchanges: [] }));

    const fixture = render();

    const messages = Array.from(fixture.nativeElement.querySelectorAll('p.error')) as HTMLElement[];
    const exchangesMessage = messages.find((m) => m.textContent?.includes('service "rabbitmq" is not running'));
    expect(exchangesMessage?.getAttribute('role')).toBe('alert');
  });

  it('marks the permissions broker-unreachable message as an alert', () => {
    host.brokerPermissions.mockReturnValue(of({ ...permissions, reachable: false, error: 'service "rabbitmq" is not running', permissions: [] }));

    const fixture = render();

    const messages = Array.from(fixture.nativeElement.querySelectorAll('p.error')) as HTMLElement[];
    const permissionsMessage = messages.find((m) => m.textContent?.includes('service "rabbitmq" is not running'));
    expect(permissionsMessage?.getAttribute('role')).toBe('alert');
  });

  it('refresh reads all three again', () => {
    const fixture = render();
    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();

    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(2);
    expect(host.brokerPermissions).toHaveBeenCalledTimes(2);
  });

  it('a failed read shows the host error and keeps the last queues on screen', () => {
    const fixture = render();
    host.brokerQueues.mockReturnValueOnce(throwError(() => ({ error: { title: 'Server error', detail: 'boom' } })));

    fixture.componentInstance.refresh();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('boom');
    expect(rows(fixture, 'queues').length).toBe(queues.queues.length);
  });

  it('a failed exchanges read is not cleared by a queues read that succeeds after it', async () => {
    vi.useFakeTimers();
    const fixture = render();
    // The tick's two reads answer when told to, exchanges failing first: a synchronous mock would
    // run the queues success first, and a queues path that cleared exchangesError would still pass.
    const slowQueues = new Subject<QueuesView>();
    const slowExchanges = new Subject<ExchangesView>();
    host.brokerQueues.mockReturnValue(slowQueues.asObservable());
    host.brokerExchanges.mockReturnValue(slowExchanges.asObservable());

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    slowExchanges.error({ error: { title: 'Server error', detail: 'exchanges boom' } });
    slowQueues.next(queues);
    slowQueues.complete();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('exchanges boom');
  });

  it('auto-refresh re-reads queues, exchanges and permissions every 5 seconds until turned off', async () => {
    vi.useFakeTimers();
    const fixture = render();
    host.brokerQueues.mockClear();
    host.brokerExchanges.mockClear();
    host.brokerPermissions.mockClear();

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    await vi.advanceTimersByTimeAsync(5000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(2);
    expect(host.brokerPermissions).toHaveBeenCalledTimes(2);

    fixture.componentInstance.setAutoRefresh(false);
    await vi.advanceTimersByTimeAsync(15000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(2);
  });

  it('auto-refresh does not stack a read on one still in flight', async () => {
    vi.useFakeTimers();
    const slow = new Subject<QueuesView>();
    const fixture = render();
    host.brokerQueues.mockClear();
    host.brokerQueues.mockReturnValue(slow.asObservable());

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(15000);

    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
  });

  it('leaving the screen stops auto-refresh', async () => {
    vi.useFakeTimers();
    const fixture = render();
    fixture.componentInstance.setAutoRefresh(true);
    host.brokerQueues.mockClear();

    fixture.destroy();
    await vi.advanceTimersByTimeAsync(15000);

    expect(host.brokerQueues).not.toHaveBeenCalled();
  });

  it('refresh is not re-entrant while a read is still out', () => {
    const slow = new Subject<QueuesView>();
    host.brokerQueues.mockReturnValueOnce(of(queues)).mockReturnValueOnce(slow.asObservable());
    const fixture = render();

    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect((fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).disabled).toBe(true);
    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(2);

    slow.next(queues);
    slow.complete();
    fixture.detectChanges();

    expect((fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).disabled).toBe(false);
    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();
    expect(host.brokerQueues).toHaveBeenCalledTimes(3);
  });

  it('auto-refresh does not start while the initial queues read is still out', async () => {
    vi.useFakeTimers();
    const slow = new Subject<QueuesView>();
    host.brokerQueues.mockReturnValue(slow.asObservable());
    const fixture = render();

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(15000);

    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
  });

  it('Refresh is disabled while an automatic refresh is out, and starts nothing', async () => {
    vi.useFakeTimers();
    const fixture = render();
    host.brokerQueues.mockClear();
    host.brokerExchanges.mockClear();
    const slowQueues = new Subject<QueuesView>();
    host.brokerQueues.mockReturnValue(slowQueues.asObservable());

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    fixture.detectChanges();
    const button = fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement;
    expect(button.disabled).toBe(true);

    button.click();
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(1);

    slowQueues.next(queues);
    slowQueues.complete();
    fixture.detectChanges();
    expect(button.disabled).toBe(false);
  });

  it('reads queues again on the next tick after a held read completes', async () => {
    vi.useFakeTimers();
    const fixture = render();
    host.brokerQueues.mockClear();
    const slow = new Subject<QueuesView>();
    host.brokerQueues.mockReturnValueOnce(slow.asObservable());

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(5000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);

    slow.next(queues);
    slow.complete();

    await vi.advanceTimersByTimeAsync(5000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
  });

  it('a failed read still clears refreshing, so Refresh is enabled again', () => {
    const slow = new Subject<QueuesView>();
    host.brokerQueues.mockReturnValueOnce(of(queues)).mockReturnValueOnce(slow.asObservable());
    const fixture = render();
    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect((fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).disabled).toBe(true);

    slow.error({ error: { detail: 'boom' } });
    fixture.detectChanges();

    expect((fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).disabled).toBe(false);
  });
});
