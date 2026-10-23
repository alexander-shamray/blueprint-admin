import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { brokerHost, rows } from '../broker-testing';
import { BindingsWidget } from './bindings-widget';
import { ExchangesWidget } from './exchanges-widget';
import { PermissionsWidget } from './permissions-widget';

describe('the Exchanges, Bindings and Permissions widgets', () => {
  let host: ReturnType<typeof brokerHost>;

  beforeEach(() => {
    vi.useFakeTimers();
    host = brokerHost();
    TestBed.configureTestingModule({ providers: [{ provide: HostClient, useValue: host }] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function render<T>(type: new () => T, config: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(type);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    return fixture;
  }

  it('lists exchanges, naming the default exchange, and permissions per user', () => {
    expect(rows(render(ExchangesWidget), 'exchanges')).toEqual(['(default) direct', 'ordering-fulfilment-saga_delay x-delayed-message']);
    expect(rows(render(PermissionsWidget), 'permissions')).toEqual(['catalog-svc ^(MassTransit:) ^(MassTransit:) ^(MassTransit:)']);
  });

  it('lists bindings by source, naming the default exchange, with the kind and routing key of each', () => {
    expect(rows(render(BindingsWidget), 'bindings')).toEqual([
      '(default) ordering-catalog-events queue ordering-catalog-events',
      'Common.Contracts.Catalog.V1:PriceChanged ordering-catalog-events exchange',
      'ordering-catalog-events ordering-catalog-events queue',
    ]);
  });

  it('filters each by its text', () => {
    expect(rows(render(ExchangesWidget, { filter: 'DELAY' }), 'exchanges')).toEqual(['ordering-fulfilment-saga_delay x-delayed-message']);
    expect(rows(render(BindingsWidget, { filter: 'pricechanged' }), 'bindings').length).toBe(1);
    expect(rows(render(PermissionsWidget, { filter: 'nobody' }), 'permissions')).toEqual([]);
  });

  it('marks each broker-unreachable message as an alert, and shows no table', () => {
    const view = { reachable: false, error: 'rabbitmqctl did not answer' };
    host.brokerExchanges.mockReturnValue(of({ ...view, exchanges: [] }));
    host.brokerBindings.mockReturnValue(of({ ...view, bindings: [] }));
    host.brokerPermissions.mockReturnValue(of({ ...view, permissions: [] }));

    for (const type of [ExchangesWidget, BindingsWidget, PermissionsWidget]) {
      const fixture = render(type as new () => unknown);
      const alert = fixture.nativeElement.querySelector('p.error[role="alert"]') as HTMLElement;
      expect(alert.textContent).toContain('rabbitmqctl did not answer');
      expect(fixture.nativeElement.querySelector('table')).toBeNull();
    }
  });

  it('keeps each read\'s failure its own: a failed exchanges read is not cleared by a bindings read that succeeds', () => {
    host.brokerExchanges.mockReturnValue(throwError(() => ({ error: { detail: 'exchanges failed' } })));
    const exchanges = render(ExchangesWidget);
    const bindings = render(BindingsWidget);

    expect(exchanges.nativeElement.querySelector('p.error')?.textContent).toContain('exchanges failed');
    expect(bindings.nativeElement.querySelector('p.error')).toBeNull();
    expect(rows(bindings, 'bindings').length).toBe(3);
  });

  it('polls only while its own auto-refresh is on, each on its own feed', async () => {
    render(ExchangesWidget, { autoRefresh: true });
    render(PermissionsWidget);
    await vi.advanceTimersByTimeAsync(0);
    const exchangesAt = host.brokerExchanges.mock.calls.length;
    const permissionsAt = host.brokerPermissions.mock.calls.length;

    await vi.advanceTimersByTimeAsync(10_000);

    expect(host.brokerExchanges.mock.calls.length).toBe(exchangesAt + 2);
    expect(host.brokerPermissions.mock.calls.length).toBe(permissionsAt);
    expect(host.brokerBindings).not.toHaveBeenCalled();
  });

  it('refresh reads again', () => {
    const fixture = render(BindingsWidget);

    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();

    expect(host.brokerBindings).toHaveBeenCalledTimes(2);
  });
});
