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
  TokenClockView,
} from '../../core/host/host-types';
import { DRAIN_POLL_MS, DRAIN_WATCH_MS, UUID } from '../api/api-page';
import { ScenarioLauncher } from './scenario-launcher';
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
    op({
      id: 'bff:GetOrder',
      method: 'GET',
      url: 'http://localhost:5000/bff/v1/orders/{id}',
      pathParameters: [{ name: 'id', required: true, type: 'string' }],
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

const placedAt = '2026-10-05T12:00:00Z';

/** The BFF's order detail as far as a watch reads it, at a status with the timeline that goes with it. */
function order(status: string, steps: Record<string, string | null> = {}): string {
  return JSON.stringify({
    orderId,
    status,
    timeline: { placed: placedAt, confirmed: null, dispatched: null, delivered: null, cancelled: null, ...steps },
    asOf: '2026-10-05T12:00:09Z',
  });
}

const delivered = order('delivered', {
  confirmed: '2026-10-05T12:00:01Z',
  dispatched: '2026-10-05T12:00:06Z',
  delivered: '2026-10-05T12:00:40Z',
});

let cancelSent = false;

/**
 * Answers as the platform does for the demo user: ids for publish and order, a quote, 204 for cancel, and an
 * order read that shows the order delivered, or cancelled once a cancel has gone out.
 */
function platform(request: ProxyRequest): ProxyResult {
  const id = request.correlationId ?? '';
  if (request.url.endsWith('/catalog/products/')) return responded(200, `"${productId}"`, id);
  const quote = '{"total":19.99,"unpriced":[]}';
  if (request.url.endsWith('/checkout/quote')) return responded(200, quote, id);
  if (request.url.endsWith('/orders/')) return responded(200, `"${orderId}"`, id);
  if (request.url.endsWith(`/bff/v1/orders/${orderId}`)) {
    const cancelled = order('cancelled', { cancelled: '2026-10-05T12:00:02Z' });
    return responded(200, cancelSent ? cancelled : delivered, id);
  }
  cancelSent = true;
  return responded(204, '', id);
}

function isRead(request: ProxyRequest): boolean {
  return request.url.endsWith(`/bff/v1/orders/${orderId}`);
}

const farOff = '2099-01-01T00:00:00Z';
const heldClock: TokenClockView = {
  username: 'demo',
  held: true,
  expiresAt: farOff,
  renewsAt: farOff,
  permissions: [],
};

describe('ScenarioPage', () => {
  let host: {
    operations: ReturnType<typeof vi.fn>;
    reloadOperations: ReturnType<typeof vi.fn>;
    identityUsers: ReturnType<typeof vi.fn>;
    proxy: ReturnType<typeof vi.fn>;
    brokerQueues: ReturnType<typeof vi.fn>;
    tokenClock: ReturnType<typeof vi.fn>;
  };
  let uuids: number;

  beforeEach(() => {
    uuids = 0;
    cancelSent = false;
    host = {
      operations: vi.fn(() => of(catalog)),
      reloadOperations: vi.fn(() => of(catalog)),
      identityUsers: vi.fn(() => of([{ username: 'demo' }, { username: 'browser' }])),
      proxy: vi.fn((request: ProxyRequest) => of(platform(request))),
      brokerQueues: vi.fn(() => of(drained)),
      tokenClock: vi.fn(() => of(heldClock)),
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

  it('runs the deliver script in order, carrying the published product and the placed order forward', async () => {
    const page = render().componentInstance;

    await page.run();

    expect(page.steps().map((s) => s.key)).toEqual([
      'publish',
      'drain',
      'quote',
      'order',
      'confirmed',
      'dispatched',
      'delivered',
    ]);
    expect(page.steps().map((s) => s.state)).toEqual(['ok', 'ok', 'ok', 'ok', 'ok', 'ok', 'ok']);
    const [publish, quote, placed, ...reads] = sent();
    expect(JSON.parse(publish.body!).commandId).not.toBe(zero);
    expect(JSON.parse(quote.body!).lines[0].productId).toBe(productId);
    expect(JSON.parse(placed.body!).items[0].productId).toBe(productId);
    expect(JSON.parse(placed.body!).commandId).not.toBe(zero);
    expect(reads.map((r) => `${r.method} ${r.url}`)).toEqual(
      Array(3).fill(`GET http://localhost:5000/bff/v1/orders/${orderId}`),
    );
    expect(reads.every((r) => r.body === null)).toBe(true);
    expect(page.steps()[6].detail).toBe(
      'The order read shows delivered at 2026-10-05T12:00:40Z, 40 s after it was placed.',
    );
    expect(
      sent().every((r) => r.identity?.username === 'demo' && r.identity.password === null),
    ).toBe(true);
  });

  it('runs the cancel script: the cancel goes to the placed order and the watch reads it cancelled', async () => {
    const page = render().componentInstance;
    page.pick('cancel');

    await page.run();

    expect(page.steps().map((s) => [s.key, s.state])).toEqual([
      ['publish', 'ok'],
      ['drain', 'ok'],
      ['quote', 'ok'],
      ['order', 'ok'],
      ['cancel', 'ok'],
      ['cancelled', 'ok'],
    ]);
    expect(sent()[3].url).toBe(`http://localhost:5000/api/v1/orders/${orderId}/cancel`);
    expect(page.steps()[5].detail).toContain('cancelled at 2026-10-05T12:00:02Z, 2 s after');
  });

  it('shows a picked script as its own steps, and keeps the script it is running', async () => {
    const publish = new Subject<ProxyResult>();
    host.proxy.mockImplementation((request: ProxyRequest) =>
      request.url.endsWith('/catalog/products/') ? publish.asObservable() : of(platform(request)),
    );
    const page = render().componentInstance;
    page.pick('cancel');
    expect(page.steps().map((s) => s.key)).toContain('cancel');

    const running = page.run();
    page.pick('deliver');
    expect(page.script()).toBe('cancel');

    await vi.waitFor(() => expect(sent()).toHaveLength(1));
    publish.next(responded(200, `"${productId}"`, 'scenario-abcdef12-publish'));
    publish.complete();
    await running;
    expect(page.steps().map((s) => s.state)).toEqual(Array(6).fill('ok'));
    page.pick('deliver');

    expect(page.steps().map((s) => s.state)).toEqual(Array(7).fill('pending'));
    expect(page.runId()).toBeNull();
  });

  it('waits through a 404 and an order still placed, saying what it waits for, then reaches the step', async () => {
    vi.useFakeTimers();
    const answers = [
      responded(404, '{"code":"order.not_found"}', ''),
      responded(200, order('placed'), ''),
      responded(200, order('confirmed', { confirmed: '2026-10-05T12:00:03Z' }), ''),
    ];
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) && answers.length > 0 ? of(answers.shift()!) : of(platform(request)),
    );
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(0);
    const confirmed = () => page.steps().find((s) => s.key === 'confirmed')!;
    expect(confirmed().state).toBe('running');
    expect(confirmed().detail).toContain('Waiting for the fulfilment saga');
    expect(confirmed().detail).toContain('it has not learned of the order yet');

    await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS);
    expect(confirmed().detail).toContain('Last read: placed, as the BFF knew it at 2026-10-05T12:00:09Z.');

    await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS);
    await done;
    vi.useRealTimers();

    expect(confirmed().state).toBe('ok');
    expect(confirmed().detail).toContain('3 s after it was placed');
    expect(page.steps().map((s) => s.state)).toEqual(['ok', 'ok', 'ok', 'ok', 'ok', 'ok', 'ok']);
  });

  it('fails a watch on a status it was not waiting for and never retries it, a 401 included', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request)
        ? of(responded(401, '{"title":"Unauthorized"}', request.correlationId ?? ''))
        : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[4].state).toBe('failed');
    expect(page.steps()[4].status).toBe(401);
    expect(page.steps()[4].detail).toBe('Answered 401.');
    expect(sent().filter(isRead)).toHaveLength(1);
    expect(page.steps().slice(5).map((s) => s.state)).toEqual(['skipped', 'skipped']);
  });

  it('fails a 404 that follows an answer, since the BFF had already learned of the order', async () => {
    vi.useFakeTimers();
    const answers = [responded(200, order('placed'), ''), responded(404, '{}', '')];
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) && answers.length > 0 ? of(answers.shift()!) : of(platform(request)),
    );
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS);
    await done;
    vi.useRealTimers();

    expect(page.steps()[4].state).toBe('failed');
    expect(page.steps()[4].detail).toBe('Answered 404.');
  });

  it('stops a watch at once when the order ends in a cancellation, rather than waiting out the cap', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request)
        ? of(responded(200, order('declined', { cancelled: '2026-10-05T12:00:02Z' }), ''))
        : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[4].state).toBe('failed');
    expect(page.steps()[4].detail).toBe('The order ended declined before it was confirmed.');
    expect(sent().filter(isRead)).toHaveLength(1);
  });

  it('fails a watch whose 200 is not an order read, rather than waiting on it', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) ? of(responded(200, '{"items":[]}', '')) : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[4].state).toBe('failed');
    expect(page.steps()[4].detail).toContain('not with an order read');
  });

  it('gives up a watch at the cap and says what it last read', async () => {
    vi.useFakeTimers();
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) ? of(responded(200, order('placed'), '')) : of(platform(request)),
    );
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(DRAIN_WATCH_MS + DRAIN_POLL_MS);
    await done;
    vi.useRealTimers();

    expect(page.steps()[4].state).toBe('failed');
    expect(page.steps()[4].detail).toBe(
      `Not confirmed after ${DRAIN_WATCH_MS / 1000} s. Last read: placed, as the BFF knew it at 2026-10-05T12:00:09Z.`,
    );
    const reads = sent().filter(isRead).length;
    expect(reads).toBeGreaterThan(1);
    expect(reads).toBeLessThanOrEqual(DRAIN_WATCH_MS / DRAIN_POLL_MS + 1);
  });

  it('stops polling the order when the screen goes', async () => {
    vi.useFakeTimers();
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) ? of(responded(200, order('placed'), '')) : of(platform(request)),
    );
    const fixture = render();

    const done = fixture.componentInstance.run();
    await vi.advanceTimersByTimeAsync(0);
    const before = sent().filter(isRead).length;
    fixture.destroy();
    await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS * 3);
    await done;
    vi.useRealTimers();

    expect(sent().filter(isRead)).toHaveLength(before);
  });

  it('says so when the host is past renewing the token, and says nothing when it reuses one', async () => {
    host.tokenClock.mockImplementation(() =>
      sent().some((r) => r.url.endsWith('/orders/'))
        ? of({ ...heldClock, renewsAt: '2026-01-01T00:00:00Z' })
        : of(heldClock),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps().slice(0, 4).map((s) => s.regrant)).toEqual([null, null, null, null]);
    expect(page.steps()[4].regrant).toBe(
      'The token held for demo was due to renew at 2026-01-01T00:00:00Z, so the host minted a new one for this call.',
    );
  });

  it('says so when the token expired after the run sent, but not before its first call', async () => {
    host.tokenClock.mockImplementation(() =>
      // Nothing held before the first call, which mints; then nothing held again before the quote.
      of({ ...heldClock, held: sent().length !== 0 && sent().length !== 1 }),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[0].regrant).toBeNull();
    expect(page.steps()[2].regrant).toBe(
      'The token for demo had expired, so the host minted a new one for this call.',
    );
    expect(page.steps()[3].regrant).toBeNull();
  });

  it('sends regardless when the token clock does not answer', async () => {
    host.tokenClock.mockReturnValue(throwError(() => new Error('host busy')));
    const page = render().componentInstance;

    await page.run();

    expect(page.steps().every((s) => s.state === 'ok' && s.regrant === null)).toBe(true);
  });

  it('runs once when the palette asked for a run, and taking it leaves nothing for a later visit', async () => {
    const launcher = TestBed.inject(ScenarioLauncher);
    launcher.request();

    const page = render().componentInstance;

    await vi.waitFor(() => expect(page.steps().every((s) => s.state === 'ok')).toBe(true));
    expect(sent().filter((r) => r.correlationId?.endsWith('-publish'))).toHaveLength(1);
    expect(launcher.pending()).toBe(false);
  });

  it('drops a palette request made during a run rather than ordering again once the run ends', async () => {
    const publish = new Subject<ProxyResult>();
    host.proxy.mockImplementation((request: ProxyRequest) =>
      request.url.endsWith('/catalog/products/') ? publish.asObservable() : of(platform(request)),
    );
    const launcher = TestBed.inject(ScenarioLauncher);
    const fixture = render();
    const page = fixture.componentInstance;

    const running = page.run();
    launcher.request();
    fixture.detectChanges();
    expect(launcher.pending()).toBe(false);

    // The run reads the token clock before it sends, so the publish is subscribed a turn later.
    await vi.waitFor(() => expect(sent()).toHaveLength(1));
    publish.next(responded(200, `"${productId}"`, 'scenario-abcdef12-publish'));
    publish.complete();
    await running;
    fixture.detectChanges();

    expect(page.steps().every((s) => s.state === 'ok')).toBe(true);
    expect(sent().filter((r) => r.url.endsWith('/catalog/products/'))).toHaveLength(1);
  });

  it('drops a palette request it never acted on when the screen goes, so the next visit does not run unasked', () => {
    host.operations.mockReturnValue(new Subject<ApiCatalogView>());
    const launcher = TestBed.inject(ScenarioLauncher);
    launcher.request();

    const fixture = render();
    expect(launcher.pending()).toBe(true);
    fixture.destroy();

    expect(launcher.pending()).toBe(false);
    expect(host.proxy).not.toHaveBeenCalled();
  });

  it('gives each step that sends its own correlation id, and the drain step none', async () => {
    const page = render().componentInstance;

    await page.run();

    expect(sent().map((r) => r.correlationId)).toEqual([
      'scenario-abcdef12-publish',
      'scenario-abcdef12-quote',
      'scenario-abcdef12-order',
      'scenario-abcdef12-confirmed',
      'scenario-abcdef12-dispatched',
      'scenario-abcdef12-delivered',
    ]);
    expect(page.steps().find((s) => s.key === 'drain')?.correlationId).toBeNull();
  });

  it('stops at the first step that does not succeed and marks the rest as not run', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      of(responded(403, '{"title":"Forbidden"}', request.correlationId ?? '')),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps().map((s) => s.state)).toEqual(['failed', ...Array(6).fill('skipped')]);
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
    vi.useFakeTimers();
    const waiting = { ...drained.projection, messages: 3, drained: false };
    host.brokerQueues
      .mockReturnValueOnce(of({ ...drained, projection: waiting }))
      .mockReturnValue(throwError(() => new Error('host gone')));
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(0);
    expect(page.projection()).toEqual(waiting);
    await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS);
    await done;
    vi.useRealTimers();

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

  it('says when no realm user is known, and a reload asks for them again', () => {
    host.identityUsers.mockReturnValueOnce(throwError(() => new Error('host busy')));
    const fixture = render();
    expect(fixture.componentInstance.canRun()).toBe(false);
    expect(fixture.nativeElement.querySelector('.no-users')?.textContent).toContain(
      'No realm user',
    );

    fixture.componentInstance.reload();
    fixture.detectChanges();

    expect(fixture.componentInstance.canRun()).toBe(true);
    expect(fixture.nativeElement.querySelector('.no-users')).toBeNull();
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

  it('still cancels an order the cancel script placed while the screen was being left, and reads it no more', async () => {
    const fixture = render();
    fixture.componentInstance.pick('cancel');
    const placing = new Subject<ProxyResult>();
    host.proxy.mockImplementation((request: ProxyRequest) =>
      request.url.endsWith('/orders/') ? placing : of(platform(request)),
    );

    const done = fixture.componentInstance.run();
    await vi.waitFor(() => expect(sent()).toHaveLength(3));
    fixture.destroy();
    placing.next(responded(200, `"${orderId}"`, 'scenario-abcdef12-order'));
    await done;

    expect(sent().map((r) => r.correlationId)).toEqual([
      'scenario-abcdef12-publish',
      'scenario-abcdef12-quote',
      'scenario-abcdef12-order',
      'scenario-abcdef12-cancel',
    ]);
  });

  it('does not start the drain once the screen has gone', async () => {
    const fixture = render();
    const publish = new Subject<ProxyResult>();
    host.proxy.mockReturnValue(publish);

    const done = fixture.componentInstance.run();
    await vi.waitFor(() => expect(sent()).toHaveLength(1));
    fixture.destroy();
    publish.next(responded(200, `"${productId}"`, 'scenario-abcdef12-publish'));
    await done;

    expect(host.brokerQueues).not.toHaveBeenCalled();
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
