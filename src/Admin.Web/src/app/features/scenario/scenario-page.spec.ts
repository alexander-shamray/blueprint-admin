import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import {
  ApiCatalogView,
  ApiOperation,
  ProxyRequest,
  ProxyResult,
  QueuesView,
} from '../../core/host/host-types';
import { DRAIN_WATCH_MS, UUID } from '../api/api-page';
import { ScenarioPage } from './scenario-page';

function op(partial: Partial<ApiOperation> & { id: string }): ApiOperation {
  const [source, name] = partial.id.split(':');
  return {
    source,
    name,
    method: 'POST',
    url: 'http://localhost:5000/x',
    pathParameters: [],
    queryParameters: [],
    exampleBody: null,
    hasCommandId: false,
    edgePolicy: 'authenticated',
    available: true,
    ...partial,
  };
}

const zero = '00000000-0000-0000-0000-000000000000';
const productId = '0199a1b2-0000-7000-8000-000000000001';
const orderId = '0199a1b2-0000-7000-8000-000000000002';

const catalog: ApiCatalogView = {
  sources: [],
  operations: [
    op({
      id: 'catalog:PublishProduct',
      url: 'http://localhost:5000/api/v1/catalog/products/',
      exampleBody: `{"commandId":"${zero}","name":"Walnut desk"}`,
      hasCommandId: true,
    }),
    op({
      id: 'bff:Quote',
      url: 'http://localhost:5000/bff/v1/checkout/quote',
      exampleBody: `{"currency":"EUR","lines":[{"productId":"${zero}","quantity":1}]}`,
    }),
    op({
      id: 'ordering:PlaceOrder',
      url: 'http://localhost:5000/api/v1/orders/',
      exampleBody: `{"commandId":"${zero}","items":[{"productId":"${zero}","quantity":1}]}`,
      hasCommandId: true,
    }),
    op({
      id: 'ordering:CancelOrder',
      url: 'http://localhost:5000/api/v1/orders/{id}/cancel',
      pathParameters: [{ name: 'id', required: true, type: 'string' }],
      exampleBody: '{"reason":"customer_request"}',
    }),
  ],
};

function responded(status: number, body: string, correlationId: string): ProxyResult {
  return {
    outcome: 'responded',
    status,
    headers: {},
    body,
    bodyTruncated: false,
    bodyError: null,
    elapsedMs: 5,
    correlationId,
  };
}

const drained: QueuesView = {
  reachable: true,
  error: null,
  queues: [],
  projection: { queue: 'ordering-catalog-events', found: true, messages: 0, drained: true },
};

/** Answers as the platform does for the demo user: ids for publish and order, a quote, 204 for cancel. */
function platform(request: ProxyRequest): ProxyResult {
  const id = request.correlationId ?? '';
  if (request.url.endsWith('/catalog/products/')) return responded(200, `"${productId}"`, id);
  const quote = '{"total":19.99,"unpriced":[]}';
  if (request.url.endsWith('/checkout/quote')) return responded(200, quote, id);
  if (request.url.endsWith('/orders/')) return responded(200, `"${orderId}"`, id);
  return responded(204, '', id);
}

describe('ScenarioPage', () => {
  let host: {
    operations: ReturnType<typeof vi.fn>;
    reloadOperations: ReturnType<typeof vi.fn>;
    identityUsers: ReturnType<typeof vi.fn>;
    proxy: ReturnType<typeof vi.fn>;
    brokerQueues: ReturnType<typeof vi.fn>;
  };
  let uuids: number;

  beforeEach(() => {
    uuids = 0;
    host = {
      operations: vi.fn(() => of(catalog)),
      reloadOperations: vi.fn(() => of(catalog)),
      identityUsers: vi.fn(() => of([{ username: 'demo' }, { username: 'browser' }])),
      proxy: vi.fn((request: ProxyRequest) => of(platform(request))),
      brokerQueues: vi.fn(() => of(drained)),
    };
    TestBed.configureTestingModule({
      imports: [ScenarioPage],
      providers: [
        provideRouter([]),
        { provide: HostClient, useValue: host },
        { provide: UUID, useValue: () => `abcdef12-uuid-${++uuids}` },
      ],
    });
  });

  function render() {
    const fixture = TestBed.createComponent(ScenarioPage);
    fixture.detectChanges();
    return fixture;
  }

  function sent(): ProxyRequest[] {
    return host.proxy.mock.calls.map(([request]) => request as ProxyRequest);
  }

  it('runs the five steps in order, carrying the published product and the placed order forward', async () => {
    const page = render().componentInstance;

    await page.run();

    expect(page.steps().map((s) => s.state)).toEqual(['ok', 'ok', 'ok', 'ok', 'ok']);
    const [publish, quote, order, cancel] = sent();
    expect(JSON.parse(publish.body!).commandId).not.toBe(zero);
    expect(JSON.parse(quote.body!).lines[0].productId).toBe(productId);
    expect(JSON.parse(order.body!).items[0].productId).toBe(productId);
    expect(JSON.parse(order.body!).commandId).not.toBe(zero);
    expect(cancel.url).toBe(`http://localhost:5000/api/v1/orders/${orderId}/cancel`);
    expect(
      sent().every((r) => r.identity?.username === 'demo' && r.identity.password === null),
    ).toBe(true);
  });

  it('gives each step that sends its own correlation id, and the drain step none', async () => {
    const page = render().componentInstance;

    await page.run();

    expect(sent().map((r) => r.correlationId)).toEqual([
      'scenario-abcdef12-publish',
      'scenario-abcdef12-quote',
      'scenario-abcdef12-order',
      'scenario-abcdef12-cancel',
    ]);
    expect(page.steps().find((s) => s.key === 'drain')?.correlationId).toBeNull();
  });

  it('stops at the first step that does not succeed and marks the rest as not run', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      of(responded(403, '{"title":"Forbidden"}', request.correlationId ?? '')),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps().map((s) => s.state)).toEqual([
      'failed',
      'skipped',
      'skipped',
      'skipped',
      'skipped',
    ]);
    expect(page.steps()[0].detail).toBe('Answered 403.');
    expect(host.brokerQueues).not.toHaveBeenCalled();
  });

  it('does not order when the projection queue is not declared', async () => {
    host.brokerQueues.mockReturnValue(
      of({
        ...drained,
        projection: { ...drained.projection, found: false, messages: null, drained: false },
      }),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[1].state).toBe('failed');
    expect(page.steps()[1].detail).toContain('is not declared');
    expect(sent()).toHaveLength(1);
  });

  it('reads a publish that answers without an id as a failure, since nothing after it can proceed', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      of(responded(200, '{}', request.correlationId ?? '')),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[0].state).toBe('failed');
    expect(page.steps()[0].detail).toContain('not with the id');
  });

  it('offers no trace for a step whose token Keycloak refused, since nothing was sent', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      of({
        outcome: 'tokenRejected',
        status: 401,
        body: '{"error":"invalid_grant"}',
        correlationId: request.correlationId ?? '',
      }),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[0].state).toBe('failed');
    expect(page.steps()[0].correlationId).toBeNull();
  });

  it('does not leave an earlier snapshot beside a broker that stopped answering', async () => {
    host.brokerQueues.mockReturnValue(throwError(() => new Error('host gone')));
    const page = render().componentInstance;
    page.projection.set(drained.projection);

    await page.run();

    expect(page.steps()[1].state).toBe('failed');
    expect(page.projection()).toBeNull();
  });

  it('announces where the run ended for a screen reader', async () => {
    const fixture = render();

    await fixture.componentInstance.run();
    fixture.detectChanges();

    const live = fixture.nativeElement.querySelector('[aria-live="polite"]');
    expect(live.textContent).toContain('Run complete: every step succeeded.');
  });

  it('fails the drain and does not order when the queue never drains within the cap', async () => {
    vi.useFakeTimers();
    host.brokerQueues.mockReturnValue(
      of({ ...drained, projection: { ...drained.projection, messages: 3, drained: false } }),
    );
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(DRAIN_WATCH_MS);
    await done;
    vi.useRealTimers();

    expect(page.steps()[1].state).toBe('failed');
    expect(page.steps()[1].detail).toContain('still holds 3 messages');
    expect(sent()).toHaveLength(1);
  });

  it('drops a broker read still in flight when the cap runs out', async () => {
    vi.useFakeTimers();
    let unsubscribed = false;
    host.brokerQueues.mockReturnValue(
      new Observable<QueuesView>(() => () => (unsubscribed = true)),
    );
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(DRAIN_WATCH_MS);
    await done;
    vi.useRealTimers();

    expect(unsubscribed).toBe(true);
    expect(page.steps()[1].detail).toBe('The broker did not answer before the wait ran out.');
    expect(sent()).toHaveLength(1);
  });

  it('reloads the catalog, so a service that started later makes the run available', () => {
    const quoteless = {
      ...catalog,
      operations: catalog.operations.filter((o) => o.id !== 'bff:Quote'),
    };
    host.operations.mockReturnValue(of(quoteless));
    const page = render().componentInstance;
    expect(page.canRun()).toBe(false);

    page.reload();

    expect(page.canRun()).toBe(true);
  });

  it('keeps the trace of a call that went out but was not answered', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      of({
        outcome: 'unreached',
        error: 'timed out',
        elapsedMs: 5,
        correlationId: request.correlationId ?? '',
        sent: true,
      }),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[0].state).toBe('failed');
    expect(page.steps()[0].correlationId).toBe('scenario-abcdef12-publish');
    expect(page.steps()[1].state).toBe('skipped');
  });

  it('offers no trace for a call that never went out, since Keycloak was unreachable', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      of({
        outcome: 'unreached',
        error: 'connection refused',
        elapsedMs: 5,
        correlationId: request.correlationId ?? '',
        sent: false,
      }),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[0].state).toBe('failed');
    expect(page.steps()[0].correlationId).toBeNull();
    expect(page.steps()[1].state).toBe('skipped');
  });

  it('still cancels an order placed while the screen was being left', async () => {
    const fixture = render();
    const order = new Subject<ProxyResult>();
    host.proxy.mockImplementation((request: ProxyRequest) =>
      request.url.endsWith('/orders/') ? order : of(platform(request)),
    );

    const done = fixture.componentInstance.run();
    await vi.waitFor(() => expect(sent()).toHaveLength(3));
    fixture.destroy();
    order.next(responded(200, `"${orderId}"`, 'scenario-abcdef12-order'));
    await done;

    expect(sent().map((r) => r.correlationId)).toEqual([
      'scenario-abcdef12-publish',
      'scenario-abcdef12-quote',
      'scenario-abcdef12-order',
      'scenario-abcdef12-cancel',
    ]);
  });

  it('places no order once the screen has gone', async () => {
    const fixture = render();
    const quote = new Subject<ProxyResult>();
    host.proxy.mockImplementation((request: ProxyRequest) =>
      request.url.endsWith('/checkout/quote') ? quote : of(platform(request)),
    );

    const done = fixture.componentInstance.run();
    await vi.waitFor(() => expect(sent()).toHaveLength(2));
    fixture.destroy();
    quote.next(responded(200, '{"total":19.99,"unpriced":[]}', 'scenario-abcdef12-quote'));
    await done;

    expect(sent()).toHaveLength(2);
  });

  it('clears an earlier reload failure when a run starts', async () => {
    host.reloadOperations.mockReturnValue(throwError(() => new Error('host busy')));
    const page = render().componentInstance;
    page.reload();
    expect(page.error()).toBe('host busy');

    await page.run();

    expect(page.error()).toBeNull();
  });

  it('runs as the user picked here rather than the default', async () => {
    const page = render().componentInstance;
    page.username.set('browser');

    await page.run();

    expect(sent()[0].identity?.username).toBe('browser');
  });

  it('cannot run while an operation it needs is unavailable, and says which', () => {
    host.operations.mockReturnValue(
      of({ ...catalog, operations: catalog.operations.filter((o) => o.id !== 'bff:Quote') }),
    );
    const fixture = render();

    expect(fixture.componentInstance.canRun()).toBe(false);
    expect(fixture.nativeElement.querySelector('.missing').textContent).toContain(
      'bff:Quote is not in the operation catalog',
    );
  });
});
