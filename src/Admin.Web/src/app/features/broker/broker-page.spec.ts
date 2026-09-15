import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ExchangesView, PermissionsView, QueuesView } from '../../core/host/host-types';
import { BrokerPage } from './broker-page';

const queues: QueuesView = {
  reachable: true,
  error: null,
  queues: [
    { name: 'ordering-catalog-events', messages: 2, isErrorQueue: false },
    { name: 'ordering-catalog-events_error', messages: 1, isErrorQueue: true },
  ],
  projection: { queue: 'ordering-catalog-events', found: true, messages: 2, drained: false },
};

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
  };

  beforeEach(() => {
    host = {
      brokerQueues: vi.fn(() => of(queues)),
      brokerExchanges: vi.fn(() => of(exchanges)),
      brokerPermissions: vi.fn(() => of(permissions)),
    };
    TestBed.configureTestingModule({ imports: [BrokerPage], providers: [{ provide: HostClient, useValue: host }] });
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

    expect(rows(fixture, 'queues')).toEqual(['ordering-catalog-events 2', '[error] ordering-catalog-events_error 1']);
    expect(fixture.nativeElement.querySelector('table.queues tr.error-queue')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('app-drained-indicator')?.textContent).toContain('[waiting]');
  });

  it('lists exchanges, naming the default exchange, and permissions per user', () => {
    const fixture = render();

    expect(rows(fixture, 'exchanges')).toEqual(['(default) direct', 'ordering-fulfilment-saga_delay x-delayed-message']);
    expect(rows(fixture, 'permissions')).toEqual(['catalog-svc ^(MassTransit:) ^(MassTransit:) ^(MassTransit:)']);
  });

  it('shows the broker error when rabbitmqctl did not answer, and no indicator', () => {
    host.brokerQueues.mockReturnValue(of({ ...queues, reachable: false, error: 'service "rabbitmq" is not running', queues: [] }));

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain('The broker did not answer: service "rabbitmq" is not running');
    expect(fixture.nativeElement.querySelector('app-drained-indicator')).toBeNull();
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
    expect(rows(fixture, 'queues').length).toBe(2);
  });

  it('a failed exchanges read is not cleared by a later queues auto-refresh success', async () => {
    vi.useFakeTimers();
    host.brokerExchanges.mockReturnValueOnce(throwError(() => ({ error: { title: 'Server error', detail: 'exchanges boom' } })));
    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain('exchanges boom');

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('exchanges boom');
  });

  it('auto-refresh re-reads queues every 5 seconds, only queues, until turned off', async () => {
    vi.useFakeTimers();
    const fixture = render();
    host.brokerQueues.mockClear();
    host.brokerExchanges.mockClear();

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    await vi.advanceTimersByTimeAsync(5000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).not.toHaveBeenCalled();

    fixture.componentInstance.setAutoRefresh(false);
    await vi.advanceTimersByTimeAsync(15000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
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
});
