import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { BindingsView, DeadLetterInspection, DeadLetterPlan, ExchangesView, JobSummary, PermissionsView, ProjectionPlan, QueuesView } from '../../core/host/host-types';
import { JobEvent, SseClient } from '../../core/host/sse-client';
import { BrokerPage } from './broker-page';

const queues: QueuesView = {
  reachable: true,
  error: null,
  queues: [
    { name: 'ordering-catalog-events', messages: 2, consumer: "Ordering's price projection", parked: null, runbook: null },
    {
      name: 'ordering-catalog-events_error',
      messages: 1,
      consumer: "Ordering's price projection",
      parked: 'error',
      runbook: 'https://example.test/runbooks/error-queue.md',
    },
    {
      name: 'bff-order-events_skipped',
      messages: 1,
      consumer: "the BFF's order projection",
      parked: 'skipped',
      runbook: 'https://example.test/runbooks/skipped-queue.md',
    },
    {
      name: 'payments-events_skipped',
      messages: 0,
      consumer: "Payments' order events",
      parked: 'skipped',
      runbook: 'https://example.test/runbooks/skipped-queue.md',
    },
    { name: 'something-else', messages: 0, consumer: null, parked: null, runbook: null },
    { name: 'bff-order-events', messages: 0, consumer: "the BFF's order projection", parked: null, runbook: null },
  ],
  projection: { queue: 'ordering-catalog-events', found: true, messages: 2, drained: false },
};

const plan: ProjectionPlan = {
  queue: 'bff-order-events',
  rebuildConfirm: 'bff-replay --reset',
  limits: ['A replay reaches back one outbox window and no further.', 'A rebuild deletes the projection first.'],
};

const deadLetterPlan: DeadLetterPlan = { executeConfirm: '--execute', inspectLimit: 20, recordDir: '/data/dead-letters' };

const inspection: DeadLetterInspection = {
  queue: 'ordering-catalog-events_error',
  endpoint: 'ordering-catalog-events',
  kind: 'error',
  error: null,
  messages: [
    {
      messageId: 'm-1',
      correlationId: 'corr-1',
      messageType: 'urn:message:Common.Contracts.Catalog.V1:PriceChanged',
      faultMessage: 'no row in the price projection',
      faultExceptionType: 'System.InvalidOperationException',
      inputAddress: 'rabbitmq://rabbitmq/ordering-catalog-events',
      reason: 'fault',
      redelivered: true,
      body: '{"message":{}}',
    },
    {
      messageId: null,
      correlationId: null,
      messageType: null,
      faultMessage: null,
      faultExceptionType: null,
      inputAddress: null,
      reason: null,
      redelivered: false,
      body: null,
    },
  ],
};

const job: JobSummary = { id: 'job-7', commandLine: 'bff-replay', state: 'Running', exitCode: null, startedAt: '2026-10-15T00:00:00Z' };

const exchanges: ExchangesView = {
  reachable: true,
  error: null,
  exchanges: [{ name: '', type: 'direct' }, { name: 'ordering-fulfilment-saga_delay', type: 'x-delayed-message' }],
};

// run-locally.md's two bindings for Catalog's price changes reaching Ordering, and the default exchange's to the queue.
const bindings: BindingsView = {
  reachable: true,
  error: null,
  bindings: [
    { source: '', sourceKind: 'exchange', destination: 'ordering-catalog-events', destinationKind: 'queue', routingKey: 'ordering-catalog-events' },
    { source: 'Common.Contracts.Catalog.V1:PriceChanged', sourceKind: 'exchange', destination: 'ordering-catalog-events', destinationKind: 'exchange', routingKey: '' },
    { source: 'ordering-catalog-events', sourceKind: 'exchange', destination: 'ordering-catalog-events', destinationKind: 'queue', routingKey: '' },
  ],
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
    brokerBindings: ReturnType<typeof vi.fn>;
    brokerPermissions: ReturnType<typeof vi.fn>;
    projectionPlan: ReturnType<typeof vi.fn>;
    projectionRepair: ReturnType<typeof vi.fn>;
    projectionRebuild: ReturnType<typeof vi.fn>;
    deadLetterPlan: ReturnType<typeof vi.fn>;
    deadLetters: ReturnType<typeof vi.fn>;
    deadLetterMove: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    host = {
      brokerQueues: vi.fn(() => of(queues)),
      brokerExchanges: vi.fn(() => of(exchanges)),
      brokerBindings: vi.fn(() => of(bindings)),
      brokerPermissions: vi.fn(() => of(permissions)),
      projectionPlan: vi.fn(() => of(plan)),
      projectionRepair: vi.fn(() => of(job)),
      projectionRebuild: vi.fn(() => of(job)),
      deadLetterPlan: vi.fn(() => of(deadLetterPlan)),
      deadLetters: vi.fn(() => of(inspection)),
      deadLetterMove: vi.fn(() => of(job)),
    };
    TestBed.configureTestingModule({
      imports: [BrokerPage],
      providers: [
        provideRouter([]),
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
      "[error] ordering-catalog-events_error 1 Ordering's price projection error-queue runbook Open",
      "[skipped] bff-order-events_skipped 1 the BFF's order projection skipped-queue runbook Open",
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

  function withBffDepth(messages: number): QueuesView {
    return { ...queues, queues: queues.queues.map((q) => (q.name === 'bff-order-events' ? { ...q, messages } : q)) };
  }

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
    expect(fixture.nativeElement.querySelector('section.projection .depth')?.textContent?.trim()).toBe('[drained] bff-order-events is empty.');
  });

  it('an exit while a refresh is still out reads again once that refresh lands, since it may have read before the exit', () => {
    const events = new Subject<JobEvent>();
    TestBed.overrideProvider(SseClient, { useValue: { follow: () => events } });
    const fixture = render();
    (fixture.nativeElement.querySelector('button.repair') as HTMLButtonElement).click();
    fixture.detectChanges();
    const inFlight = new Subject<QueuesView>();
    host.brokerQueues.mockClear();
    host.brokerQueues.mockReturnValueOnce(inFlight);
    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();

    events.next({ kind: 'exited', exitCode: 0 });
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
    inFlight.next(queues);
    inFlight.complete();

    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
  });

  it('a start while a refresh is still out reads again once that refresh lands, since it may have read before the start', () => {
    const fixture = render();
    const inFlight = new Subject<QueuesView>();
    host.brokerQueues.mockClear();
    host.brokerQueues.mockReturnValueOnce(inFlight);
    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();

    (fixture.nativeElement.querySelector('button.repair') as HTMLButtonElement).click();
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
    inFlight.next(queues);
    inFlight.complete();

    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
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

  it('a refused rebuild keeps the typed confirmation, so a retry is not retyped', async () => {
    host.projectionRebuild.mockReturnValue(throwError(() => ({ error: { detail: 'A repair is running' } })));
    const fixture = render();
    const button = fixture.nativeElement.querySelector('button.rebuild') as HTMLButtonElement;
    const input = fixture.nativeElement.querySelector('.rebuild-box input') as HTMLInputElement;
    input.value = 'bff-replay --reset';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    fixture.detectChanges();

    button.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('section.projection p.error')?.textContent).toContain('A repair is running');
    expect(button.disabled).toBe(false);
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

  it('lists bindings by source, naming the default exchange, with the kind and routing key of each', () => {
    const fixture = render();

    expect(rows(fixture, 'bindings')).toEqual([
      '(default) ordering-catalog-events queue ordering-catalog-events',
      'Common.Contracts.Catalog.V1:PriceChanged ordering-catalog-events exchange',
      'ordering-catalog-events ordering-catalog-events queue',
    ]);
  });

  it('marks the bindings broker-unreachable message as an alert', () => {
    host.brokerBindings.mockReturnValue(of({ ...bindings, reachable: false, error: 'service "rabbitmq" is not running', bindings: [] }));

    const fixture = render();

    expect(fixture.nativeElement.querySelector('table.bindings')).toBeNull();
    const messages = Array.from(fixture.nativeElement.querySelectorAll('p.error')) as HTMLElement[];
    const bindingsMessage = messages.find((m) => m.textContent?.includes('service "rabbitmq" is not running'));
    expect(bindingsMessage?.getAttribute('role')).toBe('alert');
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

  it('refresh reads all four again', () => {
    const fixture = render();
    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();

    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(2);
    expect(host.brokerBindings).toHaveBeenCalledTimes(2);
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

  it('auto-refresh re-reads queues, exchanges, bindings and permissions every 5 seconds until turned off', async () => {
    vi.useFakeTimers();
    const fixture = render();
    host.brokerQueues.mockClear();
    host.brokerExchanges.mockClear();
    host.brokerBindings.mockClear();
    host.brokerPermissions.mockClear();

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    await vi.advanceTimersByTimeAsync(5000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(2);
    expect(host.brokerBindings).toHaveBeenCalledTimes(2);
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

  describe('dead letters', () => {
    function click(fixture: { nativeElement: HTMLElement; detectChanges: () => void }, selector: string): void {
      (fixture.nativeElement.querySelector(selector) as HTMLButtonElement).click();
      fixture.detectChanges();
    }

    function type(fixture: { nativeElement: HTMLElement; detectChanges: () => void }, value: string): void {
      const input = fixture.nativeElement.querySelector('.execute-box input') as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    }

    it('offers to open only a parked queue that holds a message', () => {
      const fixture = render();
      const buttons = Array.from(fixture.nativeElement.querySelectorAll('button.open-dead-letters')) as HTMLButtonElement[];

      expect(buttons.map((b) => b.getAttribute('aria-label'))).toEqual([
        'Open the messages on ordering-catalog-events_error',
        'Open the messages on bff-order-events_skipped',
      ]);
    });

    it('offers no queue to open when the host did not give the plan', () => {
      host.deadLetterPlan.mockReturnValue(throwError(() => new Error('down')));
      const fixture = render();

      expect(fixture.nativeElement.querySelector('button.open-dead-letters')).toBeNull();
    });

    it('opens a queue into its messages, the correlation id a link into Trace', () => {
      const fixture = render();
      click(fixture, 'button.open-dead-letters');

      expect(host.deadLetters).toHaveBeenCalledWith('ordering-catalog-events_error');
      const articles = Array.from(fixture.nativeElement.querySelectorAll('article.dead-letter')) as HTMLElement[];
      expect(articles.length).toBe(2);
      expect(articles[0].textContent).toContain('System.InvalidOperationException: no row in the price projection');
      expect(articles[0].querySelector('a')?.getAttribute('href')).toBe('/trace/corr-1');
      expect(articles[1].textContent).toContain('(no message id)');
      expect(articles[1].querySelector('button.dry-replay')).toBeNull();
    });

    it('says the reason the tool gave when it read nothing', () => {
      host.deadLetters.mockReturnValue(of({ ...inspection, messages: [], error: 'dead_letters: the broker refused the credential' }));
      const fixture = render();
      click(fixture, 'button.open-dead-letters');

      expect(fixture.nativeElement.querySelector('.dead-letters .error')?.textContent).toContain('the broker refused the credential');
    });

    it('offers the real run only after a dry run, and only with the typed confirmation', () => {
      const fixture = render();
      click(fixture, 'button.open-dead-letters');

      expect(fixture.nativeElement.querySelector('button.execute')).toBeNull();
      click(fixture, 'button.dry-replay');
      expect(host.deadLetterMove).toHaveBeenCalledWith('ordering-catalog-events_error', 'replay', ['m-1'], null);
      expect(fixture.nativeElement.querySelector('app-output-pane')).not.toBeNull();

      const execute = () => fixture.nativeElement.querySelector('button.execute') as HTMLButtonElement;
      expect(execute().textContent?.trim()).toBe('Replay for real');
      expect(execute().disabled).toBe(true);
      type(fixture, '--execut');
      expect(execute().disabled).toBe(true);
      type(fixture, '--execute');
      expect(execute().disabled).toBe(false);

      click(fixture, 'button.execute');
      expect(host.deadLetterMove).toHaveBeenLastCalledWith('ordering-catalog-events_error', 'replay', ['m-1'], '--execute');
      expect(fixture.nativeElement.querySelector('button.execute')).toBeNull();
    });

    it('says how many messages a read shows, so a deeper queue is not taken as all of it', () => {
      const fixture = render();
      click(fixture, 'button.open-dead-letters');

      expect(fixture.nativeElement.querySelector('.dead-letters p')?.textContent).toContain('The first 20 messages');
    });

    it('drops what it listed once the real run is sent, and reads the queue again on asking', () => {
      const fixture = render();
      click(fixture, 'button.open-dead-letters');
      click(fixture, 'button.dry-replay');
      type(fixture, '--execute');
      click(fixture, 'button.execute');

      expect(fixture.nativeElement.querySelectorAll('article.dead-letter').length).toBe(0);
      expect(fixture.nativeElement.querySelector('.moved')?.textContent).toContain('m-1 went to the real run');
      click(fixture, 'button.reread');
      expect(host.deadLetters).toHaveBeenCalledTimes(2);
      expect(fixture.nativeElement.querySelectorAll('article.dead-letter').length).toBe(2);
      expect(fixture.nativeElement.querySelector('.moved')).toBeNull();
    });

    it("opening another queue does not show the last queue's job", () => {
      const fixture = render();
      click(fixture, 'button.open-dead-letters');
      click(fixture, 'button.dry-replay');
      expect(fixture.nativeElement.querySelector('.dead-letters app-output-pane')).not.toBeNull();

      (fixture.nativeElement.querySelectorAll('button.open-dead-letters')[1] as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.dead-letters app-output-pane')).toBeNull();
    });

    it('a dry run of the other verb re-arms the real run for that verb', () => {
      const fixture = render();
      click(fixture, 'button.open-dead-letters');
      click(fixture, 'button.dry-replay');
      click(fixture, 'button.dry-discard');

      expect((fixture.nativeElement.querySelector('button.execute') as HTMLButtonElement).textContent?.trim()).toBe('Discard for real');
    });

    it('says why the host refused a move, and arms nothing', () => {
      host.deadLetterMove.mockReturnValue(throwError(() => ({ error: { detail: 'The operator\'s password is set for one run at a time.' } })));
      const fixture = render();
      click(fixture, 'button.open-dead-letters');
      click(fixture, 'button.dry-replay');

      expect(fixture.nativeElement.querySelector('.dead-letters .error')?.textContent).toContain('one run at a time');
      expect(fixture.nativeElement.querySelector('button.execute')).toBeNull();
    });
  });
});
