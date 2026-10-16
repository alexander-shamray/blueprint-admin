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
  ReviewFlagView,
  TokenClockView,
} from '../../core/host/host-types';
import { DRAIN_POLL_MS, DRAIN_WATCH_MS, UUID } from '../api/api-page';
import { ScenarioLauncher } from './scenario-launcher';
import { ScenarioPage } from './scenario-page';
import {
  CARRIER_POLL_MS,
  CARRIER_WATCH_MS,
  RESERVATION_POLL_MS,
  REVIEW_POLL_MS,
  REVIEW_WATCH_MS,
} from './scenario-run';

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
      id: 'bff:IssueProductImageUpload',
      url: 'http://localhost:5000/bff/v1/product-images',
      exampleBody: '{"contentType":"image/png","sizeBytes":69}',
    }),
    op({
      id: 'catalog:GetProduct',
      method: 'GET',
      url: 'http://localhost:5000/api/v1/catalog/products/{id}',
      pathParameters: [{ name: 'id', required: true, type: 'string' }],
      edgePolicy: 'anonymous',
    }),
    op({
      id: 'catalog:PublishProduct',
      url: 'http://localhost:5000/api/v1/catalog/products/',
      exampleBody: `{"commandId":"${zero}","name":"Walnut desk","amount":19.99}`,
      hasCommandId: true,
    }),
    op({
      id: 'inventory:SetOnHand',
      method: 'PUT',
      url: 'http://localhost:5000/api/v1/inventory/stock/{productId}',
      pathParameters: [{ name: 'productId', required: true, type: 'string' }],
      exampleBody: '{"onHand":7}',
    }),
    op({
      id: 'bff:Quote',
      url: 'http://localhost:5000/bff/v1/checkout/quote',
      exampleBody: `{"currency":"EUR","lines":[{"productId":"${zero}","quantity":1}]}`,
    }),
    op({
      id: 'ordering:PlaceOrder',
      url: 'http://localhost:5000/api/v1/orders/',
      exampleBody: `{"commandId":"${zero}","items":[{"productId":"${zero}","quantity":1}],"shippingAddress":{"line1":"1 Test Street","city":"Almaty","postalCode":"050000","country":"KZ"}}`,
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
    op({
      id: 'inventory:GetReservation',
      method: 'GET',
      url: 'http://localhost:5000/api/v1/inventory/reservations/{orderId}',
      pathParameters: [{ name: 'orderId', required: true, type: 'string' }],
    }),
    op({
      id: 'carrier:FindBookings',
      url: 'http://localhost:5191/__admin/requests/find',
      exampleBody:
        '{"Methods":["POST"],"Path":{"Matchers":[{"Name":"ExactMatcher","Pattern":"/v1/shipments"}]},"Body":{"Matchers":[{"Name":"WildcardMatcher","Pattern":"*SIM-DOWN*"}]}}',
    }),
    op({
      id: 'carrier:FindFeedPolls',
      url: 'http://localhost:5191/__admin/requests/find',
      exampleBody:
        '{"Methods":["GET"],"Path":{"Matchers":[{"Name":"ExactMatcher","Pattern":"/v1/shipments/crr_SIM-STRANGE/events"}]}}',
    }),
  ],
};

function responded(
  status: number,
  body: string,
  correlationId: string,
  headers: Record<string, string[]> = {},
): ProxyResult {
  return {
    outcome: 'responded',
    status,
    headers,
    body,
    bodyTruncated: false,
    bodyError: null,
    elapsedMs: 5,
    correlationId,
  };
}

const uploadKey = 'uploads/0199a1b2-0000-7000-8000-0000000000aa/0199a1b2-0000-7000-8000-0000000000bb.png';
const uploadUrl = `http://127.0.0.1:8333/product-images/${uploadKey}?X-Amz-Signature=sig`;
const thumbnailUrl =
  'http://127.0.0.1:9000/products/0199a1b2-0000-7000-8000-0000000000aa/0199a1b2-0000-7000-8000-0000000000bb.png';
const issued = JSON.stringify({
  uploadUrl,
  headers: { 'Content-Type': 'image/png', 'Content-Length': '69' },
  key: uploadKey,
  expiresAt: '2026-10-15T12:05:00Z',
});
const product = JSON.stringify({ productId, name: 'Walnut desk', thumbnailUrl });
const imageHeaders = { 'Content-Type': ['image/png'], 'X-Content-Type-Options': ['nosniff'] };

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

/** Inventory's reservation read once the saga has reserved the stock (run-locally.md). */
const reserved = JSON.stringify({
  orderId,
  status: 'Reserved',
  lines: [{ productId, quantity: 1 }],
  updatedAt: '2026-10-05T12:00:01Z',
});

/** What the host answers of Ordering's `flagged for review` lines for the order: none, or one per reason. */
function flagView(flags: ReviewFlagView['flags'], rest: Partial<ReviewFlagView> = {}): ReviewFlagView {
  return { orderId, window: '30m', reachable: true, error: null, flags, ...rest };
}

function flagged(reason: string, secondsAfter = 62): ReviewFlagView['flags'][number] {
  return {
    at: new Date(Date.parse(placedAt) + secondsAfter * 1000).toISOString(),
    reason,
    line: `Order ${orderId} flagged for review: ${reason}.`,
  };
}

let cancelSent = false;

/**
 * Answers as the platform does for the demo user: ids for publish and order, a quote, 204 for cancel, and an
 * order read that shows the order delivered, or cancelled once a cancel has gone out.
 */
function platform(request: ProxyRequest): ProxyResult {
  const id = request.correlationId ?? '';
  if (request.url.endsWith('/bff/v1/product-images')) return responded(200, issued, id);
  if (request.url === thumbnailUrl) return responded(200, '', id, imageHeaders);
  if (request.url.endsWith(`/catalog/products/${productId}`)) return responded(200, product, id);
  if (request.url.endsWith('/catalog/products/')) return responded(200, `"${productId}"`, id);
  if (request.url.includes('/inventory/stock/')) return responded(204, '', id);
  if (request.url.includes('/inventory/reservations/')) return responded(200, reserved, id);
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

/** The host's `ScenarioControl.Table` as `GET /api/stack/lease` lists it. */
const leaseChanges = [
  { id: 'stop-inventory', title: 'Inventory stopped', service: 'inventory-api', description: '', maxHoldSeconds: 600 },
  { id: 'short-waits', title: "The saga's waits shortened", service: 'ordering-api', description: '', maxHoldSeconds: 600 },
];

const heldLease = {
  id: 'lease-1',
  changes: ['short-waits', 'stop-inventory'],
  title: "The saga's waits shortened + Inventory stopped",
  services: ['ordering-api', 'inventory-api'],
  label: 'Stock timeout',
  since: '2026-10-16T09:00:00Z',
  expiresAt: '2026-10-16T09:07:00Z',
  applyJob: 'job-1',
  restoreError: null,
};

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
    uploadProductImage: ReturnType<typeof vi.fn>;
    brokerQueues: ReturnType<typeof vi.fn>;
    tokenClock: ReturnType<typeof vi.fn>;
    lease: ReturnType<typeof vi.fn>;
    leaseAcquire: ReturnType<typeof vi.fn>;
    leaseAddChange: ReturnType<typeof vi.fn>;
    leaseRestore: ReturnType<typeof vi.fn>;
    reviewFlags: ReturnType<typeof vi.fn>;
    transcriptScript: ReturnType<typeof vi.fn>;
    trace: ReturnType<typeof vi.fn>;
    scenarioExport: ReturnType<typeof vi.fn>;
    transcript: ReturnType<typeof vi.fn>;
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
      uploadProductImage: vi.fn(() => of(responded(200, '', 'store'))),
      brokerQueues: vi.fn(() => of(drained)),
      tokenClock: vi.fn(() => of(heldClock)),
      lease: vi.fn(() => of({ lease: null, changes: leaseChanges })),
      leaseAcquire: vi.fn(() => of(heldLease)),
      leaseAddChange: vi.fn(() => of(heldLease)),
      leaseRestore: vi.fn(() => of({ lease: null, changes: leaseChanges })),
      reviewFlags: vi.fn(() => of(flagView([flagged('not_despatched')]))),
      transcriptScript: vi.fn(() => of('curl http://x')),
      trace: vi.fn((id: string) =>
        of({ correlationId: id, window: '30m', reachable: true, error: null, traceIds: [], tracesTruncated: false, warning: null, events: [] }),
      ),
      scenarioExport: vi.fn((markdown: string) => of(markdown)),
      transcript: vi.fn(() => of({ entries: [{ sequence: 7 }, { sequence: 9 }], dropped: 0 })),
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
      'stock',
      'quote',
      'order',
      'confirmed',
      'dispatched',
      'delivered',
    ]);
    expect(page.steps().map((s) => s.state)).toEqual(['ok', 'ok', 'ok', 'ok', 'ok', 'ok', 'ok', 'ok']);
    const [publish, stock, quote, placed, ...reads] = sent();
    expect(JSON.parse(publish.body!).commandId).not.toBe(zero);
    // A product published a moment ago has no stock, and the order would end out of stock.
    expect(stock.method).toBe('PUT');
    expect(stock.url).toBe(`http://localhost:5000/api/v1/inventory/stock/${productId}`);
    expect(JSON.parse(stock.body!)).toEqual({ onHand: 10 });
    expect(JSON.parse(quote.body!).lines[0].productId).toBe(productId);
    expect(JSON.parse(placed.body!).items[0].productId).toBe(productId);
    expect(JSON.parse(placed.body!).commandId).not.toBe(zero);
    expect(reads.map((r) => `${r.method} ${r.url}`)).toEqual(
      Array(3).fill(`GET http://localhost:5000/bff/v1/orders/${orderId}`),
    );
    expect(reads.every((r) => r.body === null)).toBe(true);
    expect(page.steps()[7].detail).toBe(
      'The order read shows delivered at 2026-10-05T12:00:40Z, 40 s after it was placed.',
    );
    // Each watch keeps the read that reached its step.
    expect(page.steps().slice(5).map((s) => [s.status, s.body])).toEqual(Array(3).fill([200, delivered]));
    expect(
      sent().every((r) => r.identity?.username === 'demo' && r.identity.password === null),
    ).toBe(true);
  });

  it("fills the saga strip from the watches' order reads", async () => {
    const page = render().componentInstance;
    expect(page.strip()).toBeNull();

    await page.run();

    const strip = page.strip()!;
    expect(strip.status).toBe('delivered');
    expect(strip.reads).toBe(3);
    expect(strip.transitions.map((t) => [t.from, t.to, t.seconds])).toEqual([
      ['placed', 'confirmed', 1],
      ['confirmed', 'dispatched', 5],
      ['dispatched', 'delivered', 34],
    ]);
    expect(strip.stalled).toBe(false);
  });

  describe("the saga's own read", () => {
    const sagaOp = op({
      id: 'ordering:GetOrderSagaState',
      method: 'GET',
      url: 'http://localhost:5000/api/v1/orders/{id}/saga',
      pathParameters: [{ name: 'id', required: true, type: 'string' }],
    });

    function answerSagaWith(status: number, body: string): void {
      host.operations.mockReturnValue(of({ ...catalog, operations: [...catalog.operations, sagaOp] }));
      host.proxy.mockImplementation((request: ProxyRequest) =>
        of(
          request.url.endsWith(`/orders/${orderId}/saga`)
            ? responded(status, body, request.correlationId ?? '')
            : platform(request),
        ),
      );
    }

    it("reads the saga straight after each of the BFF's order reads and sets its word beside the buyer's", async () => {
      answerSagaWith(404, '');
      const page = render().componentInstance;

      await page.run();

      const reads = sent().filter((r) => r.url.endsWith('/saga'));
      expect(reads.map((r) => `${r.method} ${r.url}`)).toEqual(
        Array(3).fill(`GET http://localhost:5000/api/v1/orders/${orderId}/saga`),
      );
      expect(reads.every((r) => r.body === null && r.identity?.username === 'demo')).toBe(true);
      expect(page.steps().every((s) => s.state === 'ok')).toBe(true);
      const strip = page.strip()!;
      expect(strip.saga).toEqual({ kind: 'none' });
      expect(strip.pairing).toBe('agree');
      expect(strip.statusChangedAt).toBe('2026-10-05T12:00:40Z');
    });

    it('shows a saga that disagrees with the buyer as a disagreement, and does not fail the run for it', async () => {
      answerSagaWith(
        200,
        JSON.stringify({ orderId, currentState: 'AwaitingStock', startedAt: placedAt, cancelReason: null }),
      );
      const fixture = render();
      const page = fixture.componentInstance;

      await page.run();
      fixture.detectChanges();

      expect(page.strip()!.pairing).toBe('differs');
      expect(page.steps().every((s) => s.state === 'ok')).toBe(true);
      const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
      expect(text).toContain('Saga (Ordering)');
      expect(text).toContain('AwaitingStock');
      expect(text).toContain('[disagree]');
    });

    it('says the saga could not be read, and still shows the buyer, when the read is refused', async () => {
      answerSagaWith(403, '');
      const page = render().componentInstance;

      await page.run();

      expect(page.strip()!.saga).toEqual({ kind: 'unreadable', status: 403 });
      expect(page.strip()!.pairing).toBe('unknown');
      expect(page.steps().every((s) => s.state === 'ok')).toBe(true);
    });

    it('shows only the buyer, and says why, when the catalog has no saga read', async () => {
      const fixture = render();

      await fixture.componentInstance.run();
      fixture.detectChanges();

      expect(fixture.componentInstance.strip()!.saga).toBeNull();
      expect((fixture.nativeElement as HTMLElement).textContent).toContain("saga's own read is not in the operation catalog");
    });
  });

  it("empties the strip when the next run starts, so a run never shows the last one's order", async () => {
    const page = render().componentInstance;
    await page.run();
    host.proxy.mockReturnValue(new Subject<ProxyResult>());

    void page.run();

    expect(page.strip()).toBeNull();
  });

  it("exports a finished run through the host's scrubber and downloads what comes back", async () => {
    const created: Blob[] = [];
    vi.spyOn(URL, 'createObjectURL').mockImplementation((b) => (created.push(b as Blob), 'blob:x'));
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    host.scenarioExport.mockImplementation((md: string) => of(md.replace('curl http://x', '<scrubbed>')));
    const page = render().componentInstance;
    await page.run();

    await page.export();

    expect(host.scenarioExport).toHaveBeenCalledTimes(1);
    const sentDoc = host.scenarioExport.mock.calls[0][0] as string;
    expect(sentDoc).toContain('# Scenario run abcdef12: ');
    expect(sentDoc).toContain('## Trace timeline');
    expect(host.trace).toHaveBeenCalledWith('scenario-abcdef12-publish');
    // The transcript is asked for from the highest sequence it held when the run began.
    expect(host.transcriptScript).toHaveBeenCalledWith(9);
    expect(sentDoc).toContain('from when this run began');
    expect(click).toHaveBeenCalledTimes(1);
    const text = await created[0].text();
    expect(text).toContain('<scrubbed>');
    expect(text).not.toContain('curl http://x');
    expect(page.exportError()).toBeNull();
  });

  it("exports the whole session's transcript, and says so, when the run's starting point could not be read", async () => {
    vi.spyOn(URL, 'createObjectURL').mockImplementation(() => 'blob:x');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    host.transcript.mockReturnValue(throwError(() => new Error('no')));
    const page = render().componentInstance;
    await page.run();

    await page.export();

    expect(host.transcriptScript).toHaveBeenCalledWith(undefined);
    expect(host.scenarioExport.mock.calls[0][0]).toContain("The host's whole session");
  });

  it('exports nothing before a run has ended', async () => {
    const page = render().componentInstance;

    await page.export();

    expect(host.scenarioExport).not.toHaveBeenCalled();
    expect(page.finished()).toBe(false);
  });

  it('says so when the host would not scrub the export, and downloads nothing', async () => {
    const spy = vi.spyOn(URL, 'createObjectURL');
    spy.mockClear();
    host.scenarioExport.mockReturnValue(throwError(() => ({ error: { detail: 'Export too large' } })));
    const page = render().componentInstance;
    await page.run();

    await page.export();

    expect(page.exportError()).toBe('Export too large');
    expect(spy).not.toHaveBeenCalled();
  });

  it('runs the cancel script: the cancel goes to the placed order and the watch reads it cancelled', async () => {
    const page = render().componentInstance;
    page.pick('cancel');

    await page.run();

    expect(page.steps().map((s) => [s.key, s.state])).toEqual([
      ['publish', 'ok'],
      ['drain', 'ok'],
      ['stock', 'ok'],
      ['quote', 'ok'],
      ['order', 'ok'],
      ['cancel', 'ok'],
      ['cancelled', 'ok'],
    ]);
    expect(sent()[4].url).toBe(`http://localhost:5000/api/v1/orders/${orderId}/cancel`);
    expect(page.steps()[6].detail).toContain('cancelled at 2026-10-05T12:00:02Z, 2 s after');
  });

  const dispatched = order('dispatched', { confirmed: '2026-10-05T12:00:01Z', dispatched: '2026-10-05T12:00:06Z' });
  const alreadyShipped =
    '{"status":422,"code":"order.already_shipped","detail":"An order that has already shipped cannot be cancelled; raise a return instead."}';

  it('runs the in-transit script: the order goes to SIM-TRANSIT and the watch reads it despatched', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) ? of(responded(200, dispatched, '')) : of(platform(request)),
    );
    const page = render().componentInstance;
    page.pick('in-transit');

    await page.run();

    expect(page.steps().map((s) => [s.key, s.state])).toEqual([
      ['publish', 'ok'],
      ['drain', 'ok'],
      ['stock', 'ok'],
      ['quote', 'ok'],
      ['order', 'ok'],
      ['confirmed', 'ok'],
      ['dispatched', 'ok'],
    ]);
    const placed = sent()[3];
    expect(JSON.parse(placed.body!).shippingAddress).toEqual({
      line1: '1 Test Street',
      city: 'Almaty',
      postalCode: 'SIM-TRANSIT',
      country: 'KZ',
    });
    expect(page.steps()[4].detail).toBe(`Answered with id ${orderId}, to the delivery postal code SIM-TRANSIT.`);
  });

  it('says so when the example order has no address to script the carrier with', async () => {
    host.operations.mockReturnValue(
      of({
        ...catalog,
        operations: catalog.operations.map((o) =>
          o.id === 'ordering:PlaceOrder' ? { ...o, exampleBody: `{"commandId":"${zero}","items":[{"productId":"${zero}","quantity":1}]}` } : o,
        ),
      }),
    );
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) ? of(responded(200, dispatched, '')) : of(platform(request)),
    );
    const page = render().componentInstance;
    page.pick('in-transit');

    await page.run();

    expect(page.steps()[4].detail).toContain('no shippingAddress to set');
  });

  it('runs the already-shipped script: the cancel is refused 422 order.already_shipped and the order is unchanged', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request)
        ? of(responded(200, dispatched, ''))
        : request.url.endsWith('/cancel')
          ? of(responded(422, alreadyShipped, request.correlationId ?? ''))
          : of(platform(request)),
    );
    const page = render().componentInstance;
    page.pick('already-shipped');

    await page.run();

    expect(page.steps().map((s) => [s.key, s.state])).toEqual([
      ['publish', 'ok'],
      ['drain', 'ok'],
      ['stock', 'ok'],
      ['quote', 'ok'],
      ['order', 'ok'],
      ['confirmed', 'ok'],
      ['dispatched', 'ok'],
      ['late_cancel', 'ok'],
      ['unchanged', 'ok'],
    ]);
    expect(page.steps()[7].status).toBe(422);
    expect(page.steps()[7].detail).toBe('Refused with 422 order.already_shipped, as an order the carrier has collected is.');
  });

  it('fails the already-shipped script when the cancel is taken, and does not read the order back', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) ? of(responded(200, dispatched, '')) : of(platform(request)),
    );
    const page = render().componentInstance;
    page.pick('already-shipped');

    await page.run();

    expect(page.steps()[7].state).toBe('failed');
    expect(page.steps()[7].detail).toBe('Answered 204: the cancel was taken, so this order was not despatched.');
    expect(page.steps()[8].state).toBe('skipped');
  });

  it('fails the already-shipped script on a refusal that is not the despatch one', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request)
        ? of(responded(200, dispatched, ''))
        : request.url.endsWith('/cancel')
          ? of(responded(422, '{"status":422,"code":"order.not_cancellable"}', ''))
          : of(platform(request)),
    );
    const page = render().componentInstance;
    page.pick('already-shipped');

    await page.run();

    expect(page.steps()[7].state).toBe('failed');
    expect(page.steps()[7].detail).toBe('Answered 422, not the 422 order.already_shipped a despatched order is refused with.');
  });

  it.each([
    ['declined', 'declined', 10.01, 10],
    ['out-of-stock', 'out_of_stock', 19.99, 0],
  ] as const)(
    'runs the %s script: its cause is set before the order and the watch reads the refusal',
    async (script, status, amount, onHand) => {
      host.proxy.mockImplementation((request: ProxyRequest) =>
        isRead(request)
          ? of(responded(200, order(status, { cancelled: '2026-10-05T12:00:01Z' }), ''))
          : of(platform(request)),
      );
      const page = render().componentInstance;
      page.pick(script);

      await page.run();

      expect(page.steps().map((s) => [s.key, s.state])).toEqual([
        ['publish', 'ok'],
        ['drain', 'ok'],
        ['stock', 'ok'],
        ['quote', 'ok'],
        ['order', 'ok'],
        [status, 'ok'],
      ]);
      const [publish, stock, , placed] = sent();
      expect(JSON.parse(publish.body!).amount).toBe(amount);
      expect(stock.method).toBe('PUT');
      expect(stock.url).toBe(`http://localhost:5000/api/v1/inventory/stock/${productId}`);
      expect(JSON.parse(stock.body!)).toEqual({ onHand });
      expect(JSON.parse(placed.body!).items[0].productId).toBe(productId);
      expect(page.steps()[2].detail).toBe(`Answered with the stock on hand set to ${onHand}.`);
      expect(page.steps()[5].detail).toContain(`shows ${status} at 2026-10-05T12:00:01Z`);
    },
  );

  it('stops before quoting or ordering when the stock write is refused', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      request.url.includes('/inventory/stock/')
        ? of(responded(403, '', request.correlationId ?? ''))
        : of(platform(request)),
    );
    const page = render().componentInstance;
    page.pick('out-of-stock');

    await page.run();

    expect(page.steps().map((s) => s.state)).toEqual([
      'ok',
      'ok',
      'failed',
      'skipped',
      'skipped',
      'skipped',
    ]);
    expect(page.steps()[2].status).toBe(403);
    expect(sent().map((r) => r.method)).toEqual(['POST', 'PUT']);
  });

  it('stops the refusal script when the order ends some other way, saying how', async () => {
    const page = render().componentInstance;
    page.pick('declined');

    await page.run();

    expect(page.steps()[5].state).toBe('failed');
    expect(page.steps()[5].detail).toBe('The order ended delivered, not declined.');
  });

  describe('the carrier log scripts', () => {
    const confirmedOrder = order('confirmed', { confirmed: '2026-10-05T12:00:01Z' });
    const logged = (secondsAfter: number, status: number, shipmentId = 's1', method = 'POST') => ({
      Request: {
        DateTime: new Date(Date.parse(placedAt) + secondsAfter * 1000).toISOString(),
        Method: method,
        Path: method === 'POST' ? '/v1/shipments' : '/v1/shipments/crr_SIM-STRANGE/events',
        Body: method === 'POST' ? JSON.stringify({ shipmentId, address: { postalCode: 'x' } }) : null,
      },
      Response: { StatusCode: status },
    });

    /** The order reads confirmed throughout, and each find answers what `bookings` and `polls` are called for. */
    function simulator(bookings: () => unknown[], polls: () => unknown[] = () => []): void {
      host.proxy.mockImplementation((request: ProxyRequest) => {
        if (request.url.endsWith('/__admin/requests/find')) {
          const feed = request.body!.includes('"GET"');
          return of(responded(200, JSON.stringify(feed ? polls() : bookings()), ''));
        }
        return isRead(request) ? of(responded(200, confirmedOrder, '')) : of(platform(request));
      });
    }

    it.each([
      ['carrier-refused', 'SIM-REFUSED', () => [logged(2, 422)], 'The carrier refused the booking with 422.'],
      [
        'carrier-down',
        'SIM-DOWN',
        () => [logged(2, 503), logged(3, 503)],
        'The carrier saw 2 attempts at the same booking, each answered 503.',
      ],
      [
        'carrier-slow',
        'SIM-SLOW',
        () => [logged(2, 201), logged(4, 201)],
        'The carrier saw 2 attempts at the same booking, each answered 201 after Shipping had stopped waiting.',
      ],
    ] as const)('runs the %s script: the order goes to %s and the simulator\'s log shows the outcome', async (script, code, bookings, detail) => {
      simulator(bookings);
      const page = render().componentInstance;
      page.pick(script);

      await page.run();

      expect(page.steps().map((s) => [s.key, s.state])).toEqual([
        ['publish', 'ok'],
        ['drain', 'ok'],
        ['stock', 'ok'],
        ['quote', 'ok'],
        ['order', 'ok'],
        ['confirmed', 'ok'],
        ['carrier', 'ok'],
        ['held', 'ok'],
      ]);
      expect(JSON.parse(sent()[3].body!).shippingAddress.postalCode).toBe(code);
      const find = sent().find((r) => r.url.endsWith('/__admin/requests/find'))!;
      expect(JSON.parse(find.body!).Body.Matchers[0].Pattern).toBe(`*${code}*`);
      // The simulator is anonymous and untraced: nothing is sent as the user.
      expect(find.identity).toBeNull();
      expect(find.correlationId).toBeNull();
      expect(page.steps()[6].detail).toBe(detail);
      expect(page.steps()[6].correlationId).toBeNull();
    });

    it('runs the carrier-strange script: one booking, then the feed polled again and again', async () => {
      simulator(() => [logged(2, 201)], () => [logged(5, 200, 's1', 'GET'), logged(8, 200, 's1', 'GET')]);
      const page = render().componentInstance;
      page.pick('carrier-strange');

      await page.run();

      expect(page.steps().map((s) => s.state)).toEqual(['ok', 'ok', 'ok', 'ok', 'ok', 'ok', 'ok', 'ok']);
      expect(page.steps()[6].detail).toBe('The carrier booked the parcel once (201) and its feed has been polled 2 times since.');
      expect(sent().filter((r) => r.url.endsWith('/__admin/requests/find')).map((r) => JSON.parse(r.body!).Methods[0])).toEqual(['POST', 'GET']);
    });

    it('waits for the second attempt, saying what it has seen, then reaches the outcome', async () => {
      vi.useFakeTimers();
      const answers = [[logged(2, 503)], [logged(2, 503), logged(3, 503)]];
      simulator(() => (answers.length > 1 ? answers.shift()! : answers[0]));
      const page = render().componentInstance;
      page.pick('carrier-down');

      const done = page.run();
      await vi.advanceTimersByTimeAsync(0);
      const carrier = () => page.steps().find((s) => s.key === 'carrier')!;
      expect(carrier().state).toBe('running');
      expect(carrier().detail).toContain('The carrier has seen 1 attempt, answered 503');

      await vi.advanceTimersByTimeAsync(CARRIER_POLL_MS);
      await done;
      vi.useRealTimers();

      expect(carrier().state).toBe('ok');
    });

    it('fails when the carrier answered what the code does not script, naming it', async () => {
      simulator(() => [logged(2, 201)]);
      const page = render().componentInstance;
      page.pick('carrier-refused');

      await page.run();

      expect(page.steps()[6].state).toBe('failed');
      expect(page.steps()[6].detail).toBe('The carrier answered 201 to a booking attempt, not 422.');
      expect(page.steps()[7].state).toBe('skipped');
    });

    it('fails at the cap with what it last saw', async () => {
      vi.useFakeTimers();
      simulator(() => [logged(2, 503)]);
      const page = render().componentInstance;
      page.pick('carrier-down');

      const done = page.run();
      await vi.advanceTimersByTimeAsync(CARRIER_WATCH_MS + CARRIER_POLL_MS);
      await done;
      vi.useRealTimers();

      expect(page.steps()[6].state).toBe('failed');
      expect(page.steps()[6].detail).toContain(`Not shown after ${CARRIER_WATCH_MS / 1000} s. The carrier has seen 1 attempt`);
    });

    it('never reads a truncated log, or an answer that is not a list, as an empty one', async () => {
      host.proxy.mockImplementation((request: ProxyRequest) =>
        request.url.endsWith('/__admin/requests/find')
          ? of({ ...responded(200, '[{"Request":', ''), bodyTruncated: true })
          : isRead(request)
            ? of(responded(200, confirmedOrder, ''))
            : of(platform(request)),
      );
      const page = render().componentInstance;
      page.pick('carrier-down');

      await page.run();

      expect(page.steps()[6].state).toBe('failed');
      expect(page.steps()[6].detail).toBe("The simulator's answer was longer than the proxy reads, so the log cannot be judged.");

      host.proxy.mockImplementation((request: ProxyRequest) =>
        request.url.endsWith('/__admin/requests/find')
          ? of(responded(400, '{"Status":"Bad JSON"}', ''))
          : isRead(request)
            ? of(responded(200, confirmedOrder, ''))
            : of(platform(request)),
      );
      await page.run();

      expect(page.steps()[6].detail).toBe('The simulator answered 400, not a list of the requests it logged.');
    });

    it('fails the last step when the order read left confirmed', async () => {
      host.proxy.mockImplementation((request: ProxyRequest) =>
        request.url.endsWith('/__admin/requests/find')
          ? of(responded(200, JSON.stringify([logged(2, 422)]), ''))
          : isRead(request)
            ? of(responded(200, order('dispatched', { confirmed: '2026-10-05T12:00:01Z', dispatched: '2026-10-05T12:00:06Z' }), ''))
            : of(platform(request)),
      );
      const page = render().componentInstance;
      page.pick('carrier-refused');

      await page.run();

      expect(page.steps()[5].state).toBe('ok');
      expect(page.steps()[6].state).toBe('ok');
      expect(page.steps()[7].state).toBe('failed');
      expect(page.steps()[7].detail).toBe("The order reads dispatched, not confirmed: this script's cause should leave it there.");
    });
  });

  describe('the timeout scripts and the lease', () => {
    const outOfStock = order('out_of_stock', { cancelled: '2026-10-05T12:00:21Z' });

    function readsAs(body: string): void {
      host.proxy.mockImplementation((request: ProxyRequest) =>
        isRead(request) ? of(responded(200, body, '')) : of(platform(request)),
      );
    }

    it('names what Run will change before it is pressed, from the host\'s own table', async () => {
      const fixture = render();
      fixture.componentInstance.pick('stock-timeout');
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.lease-note').textContent).toBe(
        "Run changes the stack: The saga's waits shortened, then Inventory stopped (ordering-api, inventory-api). Everything is restored when the run ends, is cancelled or fails, and the host restores it by itself after 7 minutes at most.",
      );
    });

    it('says nothing before Run for a script that changes nothing', async () => {
      const fixture = render();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.lease-note')).toBeNull();
    });

    it('runs the stock-timeout script inside its lease: the changes first, the order, the watch, then the restore', async () => {
      readsAs(outOfStock);
      const calls: string[] = [];
      host.leaseAcquire.mockImplementation(() => {
        calls.push('acquire');
        return of(heldLease);
      });
      host.leaseRestore.mockImplementation(() => {
        calls.push('restore');
        return of({ lease: null, changes: leaseChanges });
      });
      host.proxy.mockImplementation((request: ProxyRequest) => {
        calls.push(`${request.method} ${request.url}`);
        return isRead(request) ? of(responded(200, outOfStock, '')) : of(platform(request));
      });
      const page = render().componentInstance;
      page.pick('stock-timeout');

      await page.run();

      expect(page.steps().map((s) => [s.key, s.state])).toEqual([
        ['lease', 'ok'],
        ['publish', 'ok'],
        ['drain', 'ok'],
        ['order', 'ok'],
        ['out_of_stock', 'ok'],
        ['restore', 'ok'],
      ]);
      expect(host.leaseAcquire).toHaveBeenCalledWith({
        changes: ['short-waits', 'stop-inventory'],
        holdSeconds: 420,
        label: 'Stock timeout',
      });
      expect(calls[0]).toBe('acquire');
      expect(calls[calls.length - 1]).toBe('restore');
      expect(page.steps()[0].detail).toBe("Held: The saga's waits shortened + Inventory stopped. The host restores it by itself at 2026-10-16T09:07:00Z.");
      expect(page.steps()[5].detail).toBe('The stack is as the run found it.');
      // No stock write and no quote: Inventory is stopped, and the cause is that nothing answers the reservation.
      expect(sent().some((r) => r.url.includes('/inventory/stock/'))).toBe(false);
    });

    it('runs the payment-timeout script at .09 under the short waits alone, with Inventory up to stock it', async () => {
      readsAs(order('declined', { cancelled: '2026-10-05T12:00:31Z' }));
      const page = render().componentInstance;
      page.pick('payment-timeout');

      await page.run();

      expect(page.steps().map((s) => [s.key, s.state])).toEqual([
        ['lease', 'ok'],
        ['publish', 'ok'],
        ['drain', 'ok'],
        ['stock', 'ok'],
        ['quote', 'ok'],
        ['order', 'ok'],
        ['declined', 'ok'],
        ['restore', 'ok'],
      ]);
      expect(host.leaseAcquire).toHaveBeenCalledWith({ changes: ['short-waits'], holdSeconds: 360, label: 'Payment timeout' });
      expect(JSON.parse(sent()[0].body!).amount).toBe(10.09);
    });

    it('restores when a step fails, and says so', async () => {
      readsAs(delivered);
      const page = render().componentInstance;
      page.pick('payment-timeout');

      await page.run();

      expect(page.steps()[6].state).toBe('failed');
      expect(page.steps()[6].detail).toBe('The order ended delivered, not declined.');
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
      expect(page.steps()[7].state).toBe('ok');
    });

    it('stops at a lease the host refuses, sends nothing, and has nothing to restore', async () => {
      host.leaseAcquire.mockReturnValue(
        throwError(() => ({ error: { detail: 'A Scenario already holds a lease (job 3). Restore it first.' } })),
      );
      const page = render().componentInstance;
      page.pick('stock-timeout');

      await page.run();

      expect(page.steps().map((s) => s.state)).toEqual(['failed', 'skipped', 'skipped', 'skipped', 'skipped', 'skipped']);
      expect(page.steps()[0].detail).toBe('A Scenario already holds a lease (job 3). Restore it first.');
      expect(page.steps()[5].detail).toBe('No lease was taken, so nothing was changed.');
      expect(host.leaseRestore).not.toHaveBeenCalled();
      expect(host.proxy).not.toHaveBeenCalled();
    });

    it('restores after a 500 from the lease, because the host may have kept the lease it could not undo', async () => {
      host.leaseAcquire.mockReturnValue(
        throwError(() => ({
          status: 500,
          error: { detail: 'docker exited with 1. They could not be undone: ... The lease is still held; Restore it from the Stack screen.' },
        })),
      );
      const page = render().componentInstance;
      page.pick('stock-timeout');

      await page.run();

      expect(page.steps()[0].state).toBe('failed');
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
      expect(page.steps()[5].state).toBe('ok');
      expect(host.proxy).not.toHaveBeenCalled();
    });

    it('does not restore after a 409, whose lease is another run\'s', async () => {
      host.leaseAcquire.mockReturnValue(throwError(() => ({ status: 409, error: { detail: 'A Scenario already holds a lease.' } })));
      const page = render().componentInstance;
      page.pick('stock-timeout');

      await page.run();

      expect(host.leaseRestore).not.toHaveBeenCalled();
      expect(page.steps()[5].detail).toBe('No lease was taken, so nothing was changed.');
    });

    it('says plainly when the restore itself failed, and that the host will try again', async () => {
      readsAs(outOfStock);
      host.leaseRestore.mockReturnValue(throwError(() => ({ error: { detail: 'docker did not answer' } })));
      const page = render().componentInstance;
      page.pick('stock-timeout');

      await page.run();

      expect(page.steps()[5].state).toBe('failed');
      expect(page.steps()[5].detail).toBe(
        'The restore failed: docker did not answer The lease is still held; the host tries again every 30 seconds, and the Stack screen has Restore now.',
      );
    });

    it('cancels from the console: the wait ends, the rest is not run, and the stack is restored', async () => {
      const read = new Subject<ProxyResult>();
      host.proxy.mockImplementation((request: ProxyRequest) =>
        isRead(request) ? read.asObservable() : of(platform(request)),
      );
      const page = render().componentInstance;
      page.pick('stock-timeout');
      const running = page.run();
      await vi.waitFor(() => expect(sent().some(isRead)).toBe(true));

      page.cancel();
      read.next(responded(200, order('placed'), ''));
      await running;

      expect(page.steps().map((s) => [s.key, s.state])).toEqual([
        ['lease', 'ok'],
        ['publish', 'ok'],
        ['drain', 'ok'],
        ['order', 'ok'],
        ['out_of_stock', 'failed'],
        ['restore', 'ok'],
      ]);
      expect(page.steps()[4].detail).toBe('Cancelled.');
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
      expect(page.running()).toBe(false);
    });

    it('does not take a lease or call the restore for a script that changes nothing', async () => {
      const page = render().componentInstance;

      await page.run();

      expect(host.leaseAcquire).not.toHaveBeenCalled();
      expect(host.leaseRestore).not.toHaveBeenCalled();
    });
  });

  describe('the despatch-timeout script', () => {
    const confirmedOrder = order('confirmed', { confirmed: '2026-10-05T12:00:01Z' });

    function readsConfirmed(): void {
      host.proxy.mockImplementation((request: ProxyRequest) =>
        isRead(request) ? of(responded(200, confirmedOrder, '')) : of(platform(request)),
      );
    }

    it('runs inside the short waits alone: the order to SIM-STRANGE, the review Ordering logged, the order still confirmed, then the restore', async () => {
      readsConfirmed();
      const page = render().componentInstance;
      page.pick('despatch-timeout');

      await page.run();

      expect(page.steps().map((s) => [s.key, s.state])).toEqual([
        ['lease', 'ok'],
        ['publish', 'ok'],
        ['drain', 'ok'],
        ['stock', 'ok'],
        ['quote', 'ok'],
        ['order', 'ok'],
        ['confirmed', 'ok'],
        ['flagged', 'ok'],
        ['held', 'ok'],
        ['restore', 'ok'],
      ]);
      expect(host.leaseAcquire).toHaveBeenCalledWith({ changes: ['short-waits'], holdSeconds: 420, label: 'Despatch timeout' });
      expect(JSON.parse(sent()[3].body!).shippingAddress.postalCode).toBe('SIM-STRANGE');
      expect(host.reviewFlags).toHaveBeenCalledWith(orderId);
      const step = page.steps()[7];
      expect(step.detail).toBe(
        `Ordering logged "Order ${orderId} flagged for review: not_despatched." at 2026-10-05T12:01:02.000Z, 62 s after the order was placed.`,
      );
      expect(step.request).toBe(`GET /api/review/${orderId}`);
      // Read from Loki through the host, not sent through the proxy as the user: nothing of it is traced.
      expect(step.correlationId).toBeNull();
      expect(sent().some((r) => r.url.includes('/api/review/'))).toBe(false);
      expect(page.steps()[9].detail).toBe('The stack is as the run found it.');
    });

    it('waits through an empty answer and one for another reason, saying what it has seen, then reaches it', async () => {
      vi.useFakeTimers();
      readsConfirmed();
      const answers = [
        flagView([]),
        flagView([flagged('stock_not_released', 40)]),
        flagView([flagged('stock_not_released', 40), flagged('not_despatched')]),
      ];
      host.reviewFlags.mockImplementation(() => of(answers.length > 1 ? answers.shift()! : answers[0]));
      const page = render().componentInstance;
      page.pick('despatch-timeout');

      const done = page.run();
      await vi.advanceTimersByTimeAsync(0);
      const flag = () => page.steps().find((s) => s.key === 'flagged')!;
      expect(flag().state).toBe('running');
      expect(flag().detail).toContain('Ordering has not logged this order as flagged for review yet.');

      await vi.advanceTimersByTimeAsync(REVIEW_POLL_MS);
      expect(flag().state).toBe('running');
      expect(flag().detail).toContain('Ordering has flagged it for review as stock_not_released, not not_despatched');

      await vi.advanceTimersByTimeAsync(REVIEW_POLL_MS);
      await done;
      vi.useRealTimers();

      expect(flag().state).toBe('ok');
      expect(host.reviewFlags).toHaveBeenCalledTimes(3);
    });

    it('fails at the cap with what it last saw, and still restores the stack', async () => {
      vi.useFakeTimers();
      readsConfirmed();
      host.reviewFlags.mockImplementation(() => of(flagView([])));
      const page = render().componentInstance;
      page.pick('despatch-timeout');

      const done = page.run();
      await vi.advanceTimersByTimeAsync(REVIEW_WATCH_MS + REVIEW_POLL_MS);
      await done;
      vi.useRealTimers();

      const flag = page.steps().find((s) => s.key === 'flagged')!;
      expect(flag.state).toBe('failed');
      expect(flag.detail).toBe('Not logged after 150 s. Ordering has not logged this order as flagged for review yet.');
      expect(page.steps().find((s) => s.key === 'held')!.state).toBe('skipped');
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
      expect(page.steps().find((s) => s.key === 'restore')!.state).toBe('ok');
    });

    it('fails when Loki cannot be asked, rather than reading that as an order nobody flagged', async () => {
      readsConfirmed();
      host.reviewFlags.mockReturnValue(of(flagView([], { reachable: false, error: 'Loki answered 502: bad gateway' })));
      const page = render().componentInstance;
      page.pick('despatch-timeout');

      await page.run();

      const flag = page.steps().find((s) => s.key === 'flagged')!;
      expect(flag.state).toBe('failed');
      expect(flag.detail).toBe('Loki could not be read: Loki answered 502: bad gateway.');
      expect(host.reviewFlags).toHaveBeenCalledTimes(1);
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
    });

    it('fails when the host itself refuses the read, saying what it said', async () => {
      readsConfirmed();
      host.reviewFlags.mockReturnValue(throwError(() => ({ error: { detail: 'Invalid window' } })));
      const page = render().componentInstance;
      page.pick('despatch-timeout');

      await page.run();

      const flag = page.steps().find((s) => s.key === 'flagged')!;
      expect(flag.state).toBe('failed');
      expect(flag.detail).toContain('Invalid window');
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
    });

    it("fails the last read when the order has moved on from confirmed, naming the script's cause", async () => {
      let reads = 0;
      host.proxy.mockImplementation((request: ProxyRequest) => {
        if (!isRead(request)) return of(platform(request));
        reads += 1;
        return of(
          responded(
            200,
            reads === 1
              ? order('confirmed', { confirmed: '2026-10-05T12:00:01Z' })
              : order('dispatched', { confirmed: '2026-10-05T12:00:01Z', dispatched: '2026-10-05T12:00:06Z' }),
            '',
          ),
        );
      });
      const page = render().componentInstance;
      page.pick('despatch-timeout');

      await page.run();

      expect(page.steps().find((s) => s.key === 'flagged')!.state).toBe('ok');
      const held = page.steps().find((s) => s.key === 'held')!;
      expect(held.state).toBe('failed');
      expect(held.detail).toBe("The order reads dispatched, not confirmed: this script's cause should leave it there.");
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
    });

    it('cancels from the console while the review is being read, and restores the stack', async () => {
      readsConfirmed();
      const read = new Subject<ReviewFlagView>();
      host.reviewFlags.mockReturnValue(read.asObservable());
      const page = render().componentInstance;
      page.pick('despatch-timeout');
      const running = page.run();
      await vi.waitFor(() => expect(host.reviewFlags).toHaveBeenCalled());

      page.cancel();
      read.next(flagView([]));
      await running;

      const flag = page.steps().find((s) => s.key === 'flagged')!;
      expect(flag.state).toBe('failed');
      expect(flag.detail).toBe('Cancelled.');
      expect(page.steps().find((s) => s.key === 'held')!.state).toBe('skipped');
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
      expect(page.running()).toBe(false);
    });
  });

  describe('the release-timeout script', () => {
    const stepsOf = (page: ScenarioPage) => page.steps().map((s) => [s.key, s.state]);

    beforeEach(() => {
      host.reviewFlags.mockReturnValue(of(flagView([flagged('stock_not_released', 31)])));
    });

    it('reserves first, then stops Inventory, then cancels, then reads the order cancelled and the review Ordering logged, then restores', async () => {
      const calls: string[] = [];
      host.leaseAcquire.mockImplementation(() => {
        calls.push('acquire');
        return of(heldLease);
      });
      host.leaseAddChange.mockImplementation((change: string) => {
        calls.push(`add ${change}`);
        return of({ ...heldLease, title: "The saga's waits shortened + Inventory stopped" });
      });
      host.leaseRestore.mockImplementation(() => {
        calls.push('restore');
        return of({ lease: null, changes: leaseChanges });
      });
      host.proxy.mockImplementation((request: ProxyRequest) => {
        if (request.url.includes('/inventory/reservations/')) calls.push('read reservation');
        if (request.url.endsWith('/cancel')) calls.push('cancel');
        return of(platform(request));
      });
      const page = render().componentInstance;
      page.pick('release-timeout');

      await page.run();

      expect(stepsOf(page)).toEqual([
        ['lease', 'ok'],
        ['publish', 'ok'],
        ['drain', 'ok'],
        ['stock', 'ok'],
        ['quote', 'ok'],
        ['order', 'ok'],
        ['reserved', 'ok'],
        ['stop', 'ok'],
        ['cancel', 'ok'],
        ['cancelled', 'ok'],
        ['flagged', 'ok'],
        ['restore', 'ok'],
      ]);
      // The lease takes the short waits alone; Inventory is added only once its stock is reserved, and before the cancel.
      expect(host.leaseAcquire).toHaveBeenCalledWith({ changes: ['short-waits'], holdSeconds: 420, label: 'Release timeout' });
      expect(host.leaseAddChange).toHaveBeenCalledWith('stop-inventory');
      expect(calls).toEqual(['acquire', 'read reservation', 'add stop-inventory', 'cancel', 'restore']);
      expect(JSON.parse(sent()[0].body!).amount).toBe(10.09);
      const reservation = page.steps()[6];
      expect(reservation.request).toBe(`GET http://localhost:5000/api/v1/inventory/reservations/${orderId}`);
      expect(reservation.detail).toBe('The reservation reads Reserved: Inventory holds the stock for the order.');
      expect(reservation.correlationId).toBe('scenario-abcdef12-reserved');
      expect(page.steps()[7].detail).toContain("Held: The saga's waits shortened + Inventory stopped.");
      expect(page.steps()[10].detail).toContain('flagged for review: stock_not_released.');
      expect(page.steps()[11].detail).toBe('The stack is as the run found it.');
      expect(sent().every((r) => r.identity?.username === 'demo')).toBe(true);
    });

    it('waits through a 404 and a reservation not yet made, saying what it saw, then reaches Reserved', async () => {
      vi.useFakeTimers();
      const answers = [
        responded(404, '{"code":"reservation.not_found"}', ''),
        responded(200, JSON.stringify({ orderId, status: 'Pending', lines: [] }), ''),
        responded(200, reserved, ''),
      ];
      host.proxy.mockImplementation((request: ProxyRequest) =>
        request.url.includes('/inventory/reservations/') ? of(answers.shift()!) : of(platform(request)),
      );
      const page = render().componentInstance;
      page.pick('release-timeout');

      const done = page.run();
      await vi.advanceTimersByTimeAsync(0);
      const reservation = () => page.steps().find((s) => s.key === 'reserved')!;
      expect(reservation().state).toBe('running');
      expect(reservation().detail).toContain('Inventory answered 404: it has made no reservation for the order yet.');

      await vi.advanceTimersByTimeAsync(RESERVATION_POLL_MS);
      expect(reservation().state).toBe('running');
      expect(reservation().detail).toContain('Last read: the reservation is Pending, not Reserved yet.');
      expect(host.leaseAddChange).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(RESERVATION_POLL_MS);
      await done;
      vi.useRealTimers();

      expect(reservation().state).toBe('ok');
      expect(host.leaseAddChange).toHaveBeenCalledTimes(1);
    });

    it('fails the reserved step on a 403, takes no change and sends no cancel, and still restores the stack', async () => {
      host.proxy.mockImplementation((request: ProxyRequest) =>
        request.url.includes('/inventory/reservations/')
          ? of(responded(403, '', request.correlationId ?? ''))
          : of(platform(request)),
      );
      const page = render().componentInstance;
      page.pick('release-timeout');

      await page.run();

      const reservation = page.steps().find((s) => s.key === 'reserved')!;
      expect(reservation.state).toBe('failed');
      expect(reservation.status).toBe(403);
      expect(reservation.detail).toBe('Answered 403.');
      expect(page.steps().find((s) => s.key === 'stop')!.state).toBe('skipped');
      expect(host.leaseAddChange).not.toHaveBeenCalled();
      expect(sent().some((r) => r.url.endsWith('/cancel'))).toBe(false);
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
      expect(page.steps().find((s) => s.key === 'restore')!.state).toBe('ok');
    });

    it('fails the stop step on a 500 from the host, cancels nothing, and still asks for the restore', async () => {
      host.leaseAddChange.mockReturnValue(
        throwError(() => ({
          status: 500,
          error: { detail: 'docker compose stop inventory-api exited with 1. The changes were undone.' },
        })),
      );
      const page = render().componentInstance;
      page.pick('release-timeout');

      await page.run();

      const stop = page.steps().find((s) => s.key === 'stop')!;
      expect(stop.state).toBe('failed');
      expect(stop.detail).toContain('docker compose stop inventory-api exited with 1. The changes were undone.');
      expect(page.steps().find((s) => s.key === 'cancel')!.state).toBe('skipped');
      expect(sent().some((r) => r.url.endsWith('/cancel'))).toBe(false);
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
      expect(page.steps().find((s) => s.key === 'restore')!.state).toBe('ok');
    });

    it('fails the stop step on a 409, which the host answers when no lease is held, and does not restore a lease that belongs to another run', async () => {
      host.leaseAddChange.mockReturnValue(throwError(() => ({ status: 409, error: { detail: 'No lease is held.' } })));
      const page = render().componentInstance;
      page.pick('release-timeout');

      await page.run();

      expect(page.steps().find((s) => s.key === 'stop')!.detail).toContain('No lease is held.');
      expect(host.leaseRestore).not.toHaveBeenCalled();
      expect(page.steps().find((s) => s.key === 'restore')!.state).toBe('skipped');
    });

    it('cancels from the console while the reservation is awaited, takes no change, and restores the stack', async () => {
      const read = new Subject<ProxyResult>();
      host.proxy.mockImplementation((request: ProxyRequest) =>
        request.url.includes('/inventory/reservations/') ? read.asObservable() : of(platform(request)),
      );
      const page = render().componentInstance;
      page.pick('release-timeout');
      const running = page.run();
      await vi.waitFor(() => expect(sent().some((r) => r.url.includes('/inventory/reservations/'))).toBe(true));

      page.cancel();
      read.next(responded(404, '', ''));
      read.complete();
      await running;

      expect(page.steps().find((s) => s.key === 'stop')!.state).toBe('skipped');
      expect(host.leaseAddChange).not.toHaveBeenCalled();
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
      expect(page.running()).toBe(false);
    });

    it('sends no cancel and reads nothing more when Cancel is pressed while the stop is made, and still restores the stack', async () => {
      const page = render().componentInstance;
      host.leaseAddChange.mockImplementation(() => {
        page.cancel();
        return of(heldLease);
      });
      page.pick('release-timeout');

      await page.run();

      expect(page.steps().find((s) => s.key === 'cancel')!.state).toBe('skipped');
      expect(page.steps().find((s) => s.key === 'cancelled')!.state).toBe('skipped');
      expect(sent().some((r) => r.url.endsWith('/cancel'))).toBe(false);
      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
    });
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
    expect(page.steps().map((s) => s.state)).toEqual(Array(7).fill('ok'));
    page.pick('deliver');

    expect(page.steps().map((s) => s.state)).toEqual(Array(8).fill('pending'));
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
    expect(page.steps().map((s) => s.state)).toEqual(['ok', 'ok', 'ok', 'ok', 'ok', 'ok', 'ok', 'ok']);
  });

  it('fails a watch on a status it was not waiting for and never retries it, a 401 included', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request)
        ? of(responded(401, '{"title":"Unauthorized"}', request.correlationId ?? ''))
        : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[5].state).toBe('failed');
    expect(page.steps()[5].status).toBe(401);
    expect(page.steps()[5].body).toBe('{"title":"Unauthorized"}');
    expect(page.steps()[5].detail).toBe('Answered 401.');
    expect(sent().filter(isRead)).toHaveLength(1);
    expect(page.steps().slice(6).map((s) => s.state)).toEqual(['skipped', 'skipped']);
  });

  it('fails a 404 that follows an answer, since the BFF had already learned of the order', async () => {
    vi.useFakeTimers();
    const answers = [
      responded(200, order('placed'), ''),
      responded(404, '{"code":"order.not_found"}', ''),
    ];
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) && answers.length > 0 ? of(answers.shift()!) : of(platform(request)),
    );
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(0);
    expect(page.steps()[5].body).toBe(order('placed'));
    await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS);
    await done;
    vi.useRealTimers();

    expect(page.steps()[5].state).toBe('failed');
    expect(page.steps()[5].detail).toBe('Answered 404.');
    // The 404 replaces the placed read's answer, so what is shown is what failed.
    expect(page.steps()[5].status).toBe(404);
    expect(page.steps()[5].body).toBe('{"code":"order.not_found"}');
  });

  it('keeps the last 404 on a watch that saw nothing else before the cap', async () => {
    vi.useFakeTimers();
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) ? of(responded(404, '{"code":"order.not_found"}', '')) : of(platform(request)),
    );
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(DRAIN_WATCH_MS + DRAIN_POLL_MS);
    await done;
    vi.useRealTimers();

    expect(page.steps()[5].state).toBe('failed');
    expect(page.steps()[5].detail).toBe(
      `Not confirmed after ${DRAIN_WATCH_MS / 1000} s. The BFF answered 404: it has not learned of the order yet.`,
    );
    expect(page.steps()[5].status).toBe(404);
    expect(page.steps()[5].body).toBe('{"code":"order.not_found"}');
  });

  it('stops a watch at once when the order ends in a cancellation, rather than waiting out the cap', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request)
        ? of(responded(200, order('declined', { cancelled: '2026-10-05T12:00:02Z' }), ''))
        : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[5].state).toBe('failed');
    expect(page.steps()[5].detail).toBe('The order ended declined before it was confirmed.');
    expect(page.steps()[5].status).toBe(200);
    expect(page.steps()[5].body).toBe(order('declined', { cancelled: '2026-10-05T12:00:02Z' }));
    expect(sent().filter(isRead)).toHaveLength(1);
  });

  it('fails a watch whose 200 is not an order read, rather than waiting on it', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) ? of(responded(200, '{"items":[]}', '')) : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[5].state).toBe('failed');
    expect(page.steps()[5].detail).toContain('not with an order read');
    expect(page.steps()[5].status).toBe(200);
    expect(page.steps()[5].body).toBe('{"items":[]}');
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

    expect(page.steps()[5].state).toBe('failed');
    expect(page.steps()[5].detail).toBe(
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
    const fixture = render();
    const page = fixture.componentInstance;

    await page.run();
    fixture.detectChanges();

    const note =
      'The token held for demo was due to renew at 2026-01-01T00:00:00Z, so the host minted a new one for this call.';
    expect(page.steps().slice(0, 5).map((s) => s.regrant)).toEqual([null, null, null, null, null]);
    expect(page.steps()[5].regrant).toBe(note);
    // The note is for the operator, so it is on the page and not only in the step's state.
    const items = fixture.nativeElement.querySelectorAll('ol.steps li.step');
    expect(items[5].querySelector('.regrant')?.textContent.trim()).toBe(note);
    expect(items[4].querySelector('.regrant')).toBeNull();
  });

  it("clears a watch's note when a later read is answered without a mint, so it names the last call", async () => {
    vi.useFakeTimers();
    host.tokenClock.mockImplementation(() =>
      sent().filter(isRead).length === 0
        ? of({ ...heldClock, renewsAt: '2026-01-01T00:00:00Z' })
        : of(heldClock),
    );
    const answers = [
      responded(200, order('placed'), ''),
      responded(200, order('confirmed', { confirmed: '2026-10-05T12:00:01Z' }), ''),
    ];
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) && answers.length > 0 ? of(answers.shift()!) : of(platform(request)),
    );
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(0);
    expect(page.steps()[5].regrant).toContain('minted a new one for this call');
    await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS);
    await done;
    vi.useRealTimers();

    expect(page.steps()[5].state).toBe('ok');
    expect(page.steps()[5].regrant).toBeNull();
  });

  it('waits out every 404 before the first answer, not only the first', async () => {
    vi.useFakeTimers();
    const notYet = () => responded(404, '{"code":"order.not_found"}', '');
    const answers = [notYet(), notYet(), notYet(), responded(200, order('placed'), '')];
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) && answers.length > 0 ? of(answers.shift()!) : of(platform(request)),
    );
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS * 4);
    await done;
    vi.useRealTimers();

    expect(answers).toHaveLength(0);
    expect(page.steps().map((s) => s.state)).toEqual(Array(8).fill('ok'));
  });

  it('says nothing was minted when the grant the clock foresaw was refused, or the host did not answer', async () => {
    const failures: (() => Observable<ProxyResult>)[] = [
      () => of({ outcome: 'tokenRejected', status: 401, body: '{}', correlationId: '' }),
      () => throwError(() => new Error('host gone')),
    ];
    for (const failure of failures) {
      host.tokenClock.mockImplementation(() =>
        of({ ...heldClock, renewsAt: '2026-01-01T00:00:00Z' }),
      );
      host.proxy.mockImplementation((request: ProxyRequest) =>
        isRead(request) ? failure() : of(platform(request)),
      );
      const page = render().componentInstance;

      await page.run();

      expect(page.steps()[5].state).toBe('failed');
      expect(page.steps()[5].regrant).toBeNull();
      // The order step, whose call did go out under a renewed token, still says so.
      expect(page.steps()[4].regrant).toContain('minted a new one for this call');
    }
  });

  it('says so when the token expired after the run sent, but not before its first call', async () => {
    host.tokenClock.mockImplementation(() =>
      // Nothing held before the first call, which mints; then nothing held again before the stock write.
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

  it('says nothing was minted when the grant the clock foresaw then failed', async () => {
    host.tokenClock.mockImplementation(() =>
      sent().some((r) => r.url.endsWith('/orders/'))
        ? of({ ...heldClock, renewsAt: '2026-01-01T00:00:00Z' })
        : of(heldClock),
    );
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request)
        ? of({
            outcome: 'unreached',
            error: 'Keycloak did not answer: connection refused',
            elapsedMs: 5,
            correlationId: request.correlationId ?? '',
            sent: false,
          })
        : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[5].state).toBe('failed');
    expect(page.steps()[5].regrant).toBeNull();
    // Its first read never left the console, so no service logged the id and there is no trace to offer.
    expect(page.steps()[5].correlationId).toBeNull();
    expect(page.steps()[5].detail).toBe(
      'No answer from the platform: Keycloak did not answer: connection refused',
    );
  });

  it('offers no trace for a watch whose first read Keycloak refused, and says nothing was sent', async () => {
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request)
        ? of({ outcome: 'tokenRejected', status: 401, body: '{}', correlationId: request.correlationId ?? '' })
        : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[5].correlationId).toBeNull();
    expect(page.steps()[5].detail).toBe('Keycloak refused demo: 401. Nothing was sent.');
  });

  it('offers no trace for a first send the host itself did not answer, since no service logged it', async () => {
    host.proxy.mockReturnValue(throwError(() => new Error('host gone')));
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[0].state).toBe('failed');
    expect(page.steps()[0].correlationId).toBeNull();
    expect(page.steps()[0].detail).toBe('host gone');
  });

  it('says when a step was reached without timing it, where the read has no placed time', async () => {
    const answers = [
      responded(200, order('confirmed', { placed: null, confirmed: '2026-10-05T12:00:01Z' }), ''),
    ];
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) && answers.length > 0 ? of(answers.shift()!) : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[5].detail).toBe('The order read shows confirmed at 2026-10-05T12:00:01Z.');
  });

  describe('a later read that never leaves the console', () => {
    /**
     * Runs the deliver script with these as the confirmed watch's reads, one poll apart, and returns
     * the step as the first read left it and as the run ended it.
     */
    async function watchWith(...reads: (() => Observable<ProxyResult>)[]) {
      vi.useFakeTimers();
      host.proxy.mockImplementation((request: ProxyRequest) =>
        isRead(request) && reads.length > 0 ? reads.shift()!() : of(platform(request)),
      );
      const page = render().componentInstance;
      const done = page.run();
      await vi.advanceTimersByTimeAsync(0);
      const first = page.steps()[5];
      await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS * 2);
      await done;
      vi.useRealTimers();
      return { first, confirmed: page.steps()[5] };
    }

    it('keeps the trace of the 404 that went before it', async () => {
      const { confirmed } = await watchWith(
        () => of(responded(404, '{"code":"order.not_found"}', '')),
        () => of({ outcome: 'tokenRejected', status: 401, body: '{}', correlationId: '' }),
      );

      expect(confirmed.state).toBe('failed');
      expect(confirmed.correlationId).toBe('scenario-abcdef12-confirmed');
    });

    it('keeps the trace when Keycloak did not answer, and says no token was minted', async () => {
      host.tokenClock.mockImplementation(() =>
        sent().filter(isRead).length > 0
          ? of({ ...heldClock, renewsAt: '2026-01-01T00:00:00Z' })
          : of(heldClock),
      );
      const { first, confirmed } = await watchWith(
        () => of(responded(200, order('placed'), '')),
        () =>
          of({
            outcome: 'unreached',
            error: 'Keycloak did not answer: connection refused',
            elapsedMs: 5,
            correlationId: '',
            sent: false,
          }),
      );

      // The waiting read is recorded on the step, so there is something for the failure to clear.
      expect(first.status).toBe(200);
      expect(first.body).toBe(order('placed'));
      expect(confirmed.state).toBe('failed');
      expect(confirmed.correlationId).toBe('scenario-abcdef12-confirmed');
      expect(confirmed.regrant).toBeNull();
      // The placed read's 200 and body are not this failure's.
      expect(confirmed.status).toBeNull();
      expect(confirmed.body).toBeNull();
    });

    it("does not leave an earlier read's mint on the step beside a read that got no answer", async () => {
      host.tokenClock.mockImplementation(() =>
        sent().filter(isRead).length === 0
          ? of({ ...heldClock, renewsAt: '2026-01-01T00:00:00Z' })
          : of(heldClock),
      );
      const failures: (() => Observable<ProxyResult>)[] = [
        () => throwError(() => new Error('host gone')),
        () => of({ outcome: 'tokenRejected', status: 401, body: '{}', correlationId: '' }),
        () =>
          of({ outcome: 'unreached', error: 'refused', elapsedMs: 5, correlationId: '', sent: false }),
      ];
      for (const failure of failures) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({
          imports: [ScenarioPage],
          providers: [
            provideRouter([]),
            { provide: HostClient, useValue: host },
            { provide: UUID, useValue: () => `abcdef12-uuid-${++uuids}` },
          ],
        });
        host.proxy.mockClear();
        const { confirmed } = await watchWith(
          () => of(responded(200, order('placed'), '')),
          failure,
        );

        expect(confirmed.state).toBe('failed');
        expect(confirmed.regrant).toBeNull();
      }
    });

    it('keeps the trace when the host itself did not answer', async () => {
      const { first, confirmed } = await watchWith(
        () => of(responded(200, order('placed'), '')),
        () => throwError(() => new Error('host gone')),
      );

      expect(first.status).toBe(200);
      expect(first.body).toBe(order('placed'));
      expect(confirmed.state).toBe('failed');
      expect(confirmed.correlationId).toBe('scenario-abcdef12-confirmed');
      expect(confirmed.detail).toBe('host gone');
      expect(confirmed.status).toBeNull();
      expect(confirmed.body).toBeNull();
    });
  });

  it('fails a later watch on a 404 at once, since an earlier watch already read the order', async () => {
    const answers = [
      responded(200, order('confirmed', { confirmed: '2026-10-05T12:00:01Z' }), ''),
      responded(404, '{"code":"order.not_found"}', ''),
    ];
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) && answers.length > 0 ? of(answers.shift()!) : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[5].state).toBe('ok');
    expect(page.steps()[6].state).toBe('failed');
    expect(page.steps()[6].detail).toBe('Answered 404.');
    expect(sent().filter(isRead)).toHaveLength(2);
  });

  it("keeps a watch's trace when a later read never leaves the console, and says only that read was not sent", async () => {
    vi.useFakeTimers();
    const answers: ProxyResult[] = [
      responded(200, order('placed'), ''),
      { outcome: 'tokenRejected', status: 401, body: '{"error":"invalid_grant"}', correlationId: '' },
    ];
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) && answers.length > 0 ? of(answers.shift()!) : of(platform(request)),
    );
    const page = render().componentInstance;

    const done = page.run();
    await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS);
    await done;
    vi.useRealTimers();

    const confirmed = page.steps()[5];
    expect(confirmed.state).toBe('failed');
    expect(confirmed.correlationId).toBe('scenario-abcdef12-confirmed');
    expect(confirmed.detail).toBe('Keycloak refused demo: 401. This read was not sent.');
    // Keycloak's answer, not the placed read's 200 and order.
    expect(confirmed.status).toBe(401);
    expect(confirmed.body).toBe('{"error":"invalid_grant"}');
  });

  it('says the token was minted for a call that went out and got no answer', async () => {
    host.tokenClock.mockImplementation(() =>
      sent().some((r) => r.url.endsWith('/orders/'))
        ? of({ ...heldClock, renewsAt: '2026-01-01T00:00:00Z' })
        : of(heldClock),
    );
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request)
        ? of({
            outcome: 'unreached',
            error: 'timed out',
            elapsedMs: 5,
            correlationId: request.correlationId ?? '',
            sent: true,
          })
        : of(platform(request)),
    );
    const page = render().componentInstance;

    await page.run();

    expect(page.steps()[5].state).toBe('failed');
    expect(page.steps()[5].regrant).toContain('so the host minted a new one for this call');
  });

  it("waits out a new run's first 404, since the last run's answer was for another order", async () => {
    const page = render().componentInstance;
    await page.run();

    const answers = [responded(404, '{"code":"order.not_found"}', '')];
    host.proxy.mockImplementation((request: ProxyRequest) =>
      isRead(request) && answers.length > 0 ? of(answers.shift()!) : of(platform(request)),
    );
    vi.useFakeTimers();
    const done = page.run();
    await vi.advanceTimersByTimeAsync(DRAIN_POLL_MS);
    await done;
    vi.useRealTimers();

    expect(answers).toHaveLength(0);
    expect(page.steps().map((s) => s.state)).toEqual(Array(8).fill('ok'));
  });

  it('starts each run afresh, so a user never minted before is not reported as expired', async () => {
    const page = render().componentInstance;
    await page.run();

    page.username.set('browser');
    host.tokenClock.mockReturnValue(of({ ...heldClock, username: 'browser', held: false }));
    await page.run();

    expect(page.steps()[0].regrant).toBeNull();
    expect(page.steps()[2].regrant).toBe(
      'The token for browser had expired, so the host minted a new one for this call.',
    );
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
      'scenario-abcdef12-stock',
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

    expect(page.steps().map((s) => s.state)).toEqual(['failed', ...Array(7).fill('skipped')]);
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
    expect(page.steps()[0].status).toBe(401);
    expect(page.steps()[0].body).toBe('{"error":"invalid_grant"}');
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
    await vi.waitFor(() => expect(sent()).toHaveLength(4));
    fixture.destroy();
    placing.next(responded(200, `"${orderId}"`, 'scenario-abcdef12-order'));
    await done;

    expect(sent().map((r) => r.correlationId)).toEqual([
      'scenario-abcdef12-publish',
      'scenario-abcdef12-stock',
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
    await vi.waitFor(() => expect(sent()).toHaveLength(3));
    fixture.destroy();
    quote.next(responded(200, '{"total":19.99,"unpriced":[]}', 'scenario-abcdef12-quote'));
    await done;

    expect(sent()).toHaveLength(3);
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

  describe('the image script', () => {
    /** The platform as it answers for the image script, with one answer replaced by `override` where it returns a result. */
    function answering(override: (request: ProxyRequest) => ProxyResult | undefined): void {
      host.proxy.mockImplementation((request: ProxyRequest) =>
        of(override(request) ?? platform(request)),
      );
    }

    function image() {
      const page = render().componentInstance;
      page.pick('image');
      return page;
    }

    it('asks for a signed upload, puts the image to the store, publishes with the key and reads the thumbnail from the edge', async () => {
      const page = image();

      await page.run();

      expect(page.steps().map((s) => [s.key, s.state])).toEqual([
        ['upload', 'ok'],
        ['publish', 'ok'],
        ['thumbnail', 'ok'],
      ]);
      expect(sent().map((r) => [r.method, r.url])).toEqual([
        ['POST', 'http://localhost:5000/bff/v1/product-images'],
        ['POST', 'http://localhost:5000/api/v1/catalog/products/'],
        ['GET', `http://localhost:5000/api/v1/catalog/products/${productId}`],
        ['HEAD', thumbnailUrl],
      ]);
      // The key the BFF returned is what the publish carries, never a URL.
      expect(JSON.parse(sent()[1].body!).imageKey).toBe(uploadKey);
      expect(host.uploadProductImage).toHaveBeenCalledWith({
        uploadUrl,
        headers: { 'Content-Type': 'image/png', 'Content-Length': '69' },
      });
      // The signature is in the query: the step names the object and not the secret.
      expect(page.steps()[0].request).toBe(`PUT http://127.0.0.1:8333/product-images/${uploadKey}`);
      expect(page.steps()[0].detail).toBe(`Signed ${uploadKey}, and the store answered 200 to the PUT.`);
      expect(page.steps()[2].detail).toBe('The edge answered 200 with an image and nosniff.');
      // The buyer's read is anonymous.
      expect(sent()[3].identity).toBeNull();
    });

    it('stops at the store when it refuses the PUT, and publishes nothing', async () => {
      host.uploadProductImage.mockReturnValue(of(responded(403, '<Error><Code>AccessDenied</Code></Error>', 'store')));
      const page = image();

      await page.run();

      expect(page.steps().map((s) => s.state)).toEqual(['failed', 'skipped', 'skipped']);
      expect(page.steps()[0].detail).toBe('The store answered 403 to the PUT.');
      expect(page.steps()[0].status).toBe(403);
      expect(sent()).toHaveLength(1);
    });

    it('says so when the store did not answer, or the host refused to send', async () => {
      host.uploadProductImage.mockReturnValue(
        of({ outcome: 'unreached', error: 'connection refused', elapsedMs: 3, correlationId: 'x', sent: true } as ProxyResult),
      );
      const page = image();
      await page.run();
      expect(page.steps()[0].detail).toBe('No answer from the object store: connection refused');

      host.uploadProductImage.mockReturnValue(throwError(() => new Error('host busy')));
      await page.run();
      expect(page.steps()[0].state).toBe('failed');
      expect(page.steps()[0].detail).toContain('host busy');
    });

    it('stops when the BFF refuses the signing, or answers with no upload to send', async () => {
      answering((r) => (r.url.endsWith('/product-images') ? responded(403, '', r.correlationId ?? '') : undefined));
      const page = image();
      await page.run();
      expect(page.steps()[0].detail).toBe('Answered 403.');
      expect(host.uploadProductImage).not.toHaveBeenCalled();

      answering((r) => (r.url.endsWith('/product-images') ? responded(200, '{"key":"k"}', r.correlationId ?? '') : undefined));
      await page.run();
      expect(page.steps()[0].detail).toBe('Answered 200, but not with the upload URL and key the next steps need.');
      expect(host.uploadProductImage).not.toHaveBeenCalled();
    });

    it('fails the thumbnail when the product carries none, since the publish dropped the image', async () => {
      answering((r) =>
        r.url.endsWith(`/catalog/products/${productId}`) ? responded(200, '{"thumbnailUrl":null}', r.correlationId ?? '') : undefined,
      );
      const page = image();

      await page.run();

      expect(page.steps().map((s) => s.state)).toEqual(['ok', 'ok', 'failed']);
      expect(page.steps()[2].detail).toBe('The product has no thumbnailUrl, so the image did not attach.');
    });

    it.each([
      [404, { 'Content-Type': ['image/png'], 'X-Content-Type-Options': ['nosniff'] }, 'The edge answered 404, not the image.'],
      [200, { 'x-content-type-options': ['nosniff'] }, 'The edge served no content type, not an image.'],
      [200, { 'Content-Type': ['text/html'], 'X-Content-Type-Options': ['nosniff'] }, 'The edge served text/html, not an image.'],
      [200, { 'Content-Type': ['image/png'] }, 'The edge did not set X-Content-Type-Options: nosniff.'],
    ])('fails the thumbnail when the edge answers %s with %o', async (status, headers, detail) => {
      answering((r) => (r.url === thumbnailUrl ? responded(status, '', r.correlationId ?? '', headers) : undefined));
      const page = image();

      await page.run();

      expect(page.steps()[2].state).toBe('failed');
      expect(page.steps()[2].detail).toBe(detail);
      expect(page.steps()[2].status).toBe(status);
    });

    it('fails the thumbnail when the edge does not answer', async () => {
      answering((r) =>
        r.url === thumbnailUrl
          ? ({ outcome: 'unreached', error: 'timed out', elapsedMs: 1, correlationId: 'x', sent: true } as ProxyResult)
          : undefined,
      );
      const page = image();

      await page.run();

      expect(page.steps()[2].detail).toBe('No answer from the edge: timed out');
    });

    it.each([
      [400, true],
      [503, false],
    ])('hints at the configured edge only when the proxy refuses the thumbnail with %s', async (status, hinted) => {
      host.proxy.mockImplementation((r: ProxyRequest) =>
        r.url === thumbnailUrl ? throwError(() => ({ status, message: `HTTP ${status}` })) : of(platform(r)),
      );
      const page = image();

      await page.run();

      expect(page.steps()[2].state).toBe('failed');
      expect(page.steps()[2].detail.includes('Admin:ImageEdgeUrl')).toBe(hinted);
    });

    it('cannot run while the signing or the product read is missing from the catalog, and says which', () => {
      host.operations.mockReturnValue(
        of({
          ...catalog,
          operations: catalog.operations.filter((o) => o.id !== 'bff:IssueProductImageUpload' && o.id !== 'catalog:GetProduct'),
        }),
      );
      const page = image();

      expect(page.canRun()).toBe(false);
      expect(page.resolved()?.missing).toEqual([
        'bff:IssueProductImageUpload is not in the operation catalog',
        'catalog:GetProduct is not in the operation catalog',
      ]);
    });
  });
});
