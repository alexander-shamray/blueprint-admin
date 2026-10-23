import { ApiCatalogView, ApiOperation, MailListView, ReviewFlagView } from '../../core/host/host-types';
import {
  CarrierEntry,
  OrderRead,
  SCENARIO_OPERATIONS,
  SCRIPTS,
  SagaRead,
  SagaWord,
  STALL_AFTER_MS,
  carrierEntries,
  carrierVerdict,
  headerOf,
  idFrom,
  hasShippingPostalCode,
  imageFault,
  isAlreadyShipped,
  isWatch,
  mailEvent,
  mailVerdict,
  orderRead,
  pairing,
  pairingText,
  reservationVerdict,
  resolveOperations,
  reviewVerdict,
  roleOf,
  sagaStrip,
  sagaText,
  sagaWord,
  secondsBetween,
  tallyMail,
  stepCorrelationId,
  thumbnailFrom,
  uploadFrom,
  verdict,
  hasField,
  withCarrierCode,
  withFeedOrder,
  withField,
  withProductId,
  withShippingPostalCode,
} from './scenario-run';

function op(id: string, available = true): ApiOperation {
  const [source, name] = id.split(':');
  return {
    id,
    source,
    name,
    method: 'POST',
    url: 'http://localhost:5000/x',
    pathParameters: [],
    queryParameters: [],
    exampleBody: null,
    hasCommandId: false,
    edgePolicy: 'authenticated',
    available,
  };
}

describe('resolveOperations', () => {
  it('finds every operation a script sends, and only those', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: Object.values(SCENARIO_OPERATIONS).map((id) => op(id)),
    };

    const deliver = resolveOperations(view, SCRIPTS.deliver);
    const cancel = resolveOperations(view, SCRIPTS.cancel);

    expect(deliver.missing).toEqual([]);
    expect(deliver.operations?.read?.id).toBe('bff:GetOrder');
    expect(deliver.operations?.cancel).toBeUndefined();
    expect(cancel.operations?.cancel?.id).toBe('ordering:CancelOrder');
  });

  it("offers the saga's read to a script that watches the order, and never holds a script back for it", () => {
    const all = Object.values(SCENARIO_OPERATIONS);
    const withSaga: ApiCatalogView = { sources: [], operations: all.map((id) => op(id)) };
    const without: ApiCatalogView = {
      sources: [],
      operations: all.filter((id) => id !== SCENARIO_OPERATIONS.saga).map((id) => op(id)),
    };
    const unavailable: ApiCatalogView = {
      sources: [],
      operations: all.map((id) => op(id, id !== SCENARIO_OPERATIONS.saga)),
    };

    expect(resolveOperations(withSaga, SCRIPTS.deliver).operations?.saga?.id).toBe('ordering:GetOrderSagaState');
    expect(resolveOperations(without, SCRIPTS.deliver).operations).not.toBeNull();
    expect(resolveOperations(without, SCRIPTS.deliver).operations?.saga).toBeUndefined();
    expect(resolveOperations(unavailable, SCRIPTS.deliver).operations?.saga).toBeUndefined();
    expect(resolveOperations(without, SCRIPTS.deliver).missing).toEqual([]);
  });

  it('does not hold a script back for an operation only the other script sends', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: Object.values(SCENARIO_OPERATIONS)
        .filter((id) => id !== SCENARIO_OPERATIONS.cancel)
        .map((id) => op(id)),
    };

    expect(resolveOperations(view, SCRIPTS.deliver).operations).not.toBeNull();
    expect(resolveOperations(view, SCRIPTS.cancel).missing).toEqual([
      'ordering:CancelOrder is not in the operation catalog',
    ]);
  });

  it('names an operation that is missing and one that is unavailable, and offers no run', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: [
        op(SCENARIO_OPERATIONS.publish),
        op(SCENARIO_OPERATIONS.quote),
        op(SCENARIO_OPERATIONS.order, false),
        op(SCENARIO_OPERATIONS.read),
      ],
    };

    const { operations, missing } = resolveOperations(view, SCRIPTS.cancel);

    expect(operations).toBeNull();
    expect(missing).toEqual([
      'inventory:SetOnHand is not in the operation catalog',
      "ordering:PlaceOrder is unavailable: its service's document did not load",
      'ordering:CancelOrder is not in the operation catalog',
    ]);
  });

  it("names the source's own error for an unavailable operation", () => {
    const view: ApiCatalogView = {
      sources: [{ name: 'ordering', documentUrl: 'u', available: false, error: 'connection refused', changes: null }],
      operations: Object.values(SCENARIO_OPERATIONS).map((id) => op(id, !id.startsWith('ordering'))),
    };

    expect(resolveOperations(view, SCRIPTS.deliver).missing[0]).toBe(
      "ordering:PlaceOrder is unavailable: its service's document did not load: connection refused",
    );
  });
});

describe('idFrom', () => {
  it('reads the bare JSON string a Result<Guid> answers with', () => {
    expect(idFrom('"0199a1b2-0000-7000-8000-000000000001"')).toBe('0199a1b2-0000-7000-8000-000000000001');
  });

  it('reads nothing else as an id', () => {
    expect(idFrom('{"id":"x"}')).toBeNull();
    expect(idFrom('""')).toBeNull();
    expect(idFrom('not json')).toBeNull();
    expect(idFrom('"not-a-guid"')).toBeNull();
  });
});

describe('withProductId', () => {
  it('sets the product on every line of a quote and every item of an order', () => {
    const quote = JSON.parse(withProductId('{"currency":"EUR","lines":[{"productId":"0","quantity":1}]}', 'p-1'));
    const order = JSON.parse(
      withProductId('{"commandId":"c","items":[{"productId":"0","quantity":1}],"currency":"EUR"}', 'p-1'),
    );

    expect(quote).toEqual({ currency: 'EUR', lines: [{ productId: 'p-1', quantity: 1 }] });
    expect(order.items).toEqual([{ productId: 'p-1', quantity: 1 }]);
    expect(order.commandId).toBe('c');
  });

  it('leaves a body that is not a JSON object as it was', () => {
    expect(withProductId('[1]', 'p-1')).toBe('[1]');
    expect(withProductId('nope', 'p-1')).toBe('nope');
  });
});

describe('stepCorrelationId', () => {
  it('stays inside the alphabet the backend adopts', () => {
    expect(stepCorrelationId('ab12cd34', 'publish')).toMatch(/^[A-Za-z0-9_-]{1,128}$/);
    expect(stepCorrelationId('ab12cd34', 'dispatched')).toMatch(/^[A-Za-z0-9_-]{1,128}$/);
  });
});

const timeline = {
  placed: '2026-10-05T12:00:00Z',
  confirmed: null,
  dispatched: null,
  delivered: null,
  cancelled: null,
};

function read(status: string, steps: Partial<OrderRead['timeline']> = {}): OrderRead {
  return { status, timeline: { ...timeline, ...steps }, asOf: '2026-10-05T12:00:09Z' };
}

describe('orderRead', () => {
  it("reads the status, the timeline and asOf from the BFF's order detail", () => {
    const body = JSON.stringify({
      orderId: 'o',
      status: 'confirmed',
      timeline: { ...timeline, confirmed: '2026-10-05T12:00:03Z' },
      asOf: '2026-10-05T12:00:03Z',
      payment: null,
    });

    expect(orderRead(body)).toEqual({
      status: 'confirmed',
      timeline: { ...timeline, confirmed: '2026-10-05T12:00:03Z' },
      asOf: '2026-10-05T12:00:03Z',
    });
  });

  it('reads nothing from a body that is not one, so the watch fails rather than waits', () => {
    expect(orderRead('not json')).toBeNull();
    expect(orderRead('[]')).toBeNull();
    expect(orderRead('{"status":"placed","asOf":"x"}')).toBeNull();
    expect(orderRead(JSON.stringify({ status: 1, timeline, asOf: 'x' }))).toBeNull();
    // Without asOf a waiting read would say "as the BFF knew it at undefined" for the whole cap.
    expect(orderRead(JSON.stringify({ status: 'placed', timeline }))).toBeNull();
    expect(orderRead(JSON.stringify({ status: 'placed', timeline: { ...timeline, placed: 5 }, asOf: 'x' }))).toBeNull();
  });
});

describe('verdict', () => {
  it('waits while the step has not happened and the order is still going', () => {
    expect(verdict('confirmed', read('placed'))).toEqual({ kind: 'waiting' });
    expect(verdict('cancelled', read('placed'))).toEqual({ kind: 'waiting' });
  });

  it('counts a step from the timeline, so a read that has moved past it still reaches it', () => {
    const delivered = read('delivered', {
      confirmed: '2026-10-05T12:00:01Z',
      dispatched: '2026-10-05T12:00:02Z',
      delivered: '2026-10-05T12:00:03Z',
    });

    expect(verdict('confirmed', delivered)).toEqual({ kind: 'reached', at: '2026-10-05T12:00:01Z' });
    expect(verdict('dispatched', delivered)).toEqual({ kind: 'reached', at: '2026-10-05T12:00:02Z' });
  });

  it('reaches a step the timeline shows even when the order was cancelled after it', () => {
    const cancelledLater = read('cancelled', {
      confirmed: '2026-10-05T12:00:01Z',
      cancelled: '2026-10-05T12:00:02Z',
    });

    expect(verdict('confirmed', cancelledLater)).toEqual({ kind: 'reached', at: '2026-10-05T12:00:01Z' });
    expect(verdict('dispatched', cancelledLater)).toEqual({
      kind: 'failed',
      reason: 'The order ended cancelled before it was dispatched.',
    });
  });

  it('fails the cancellation watch on a saga cancellation that carries its own cancel time', () => {
    for (const status of ['declined', 'out_of_stock']) {
      expect(verdict('cancelled', read(status, { cancelled: '2026-10-05T12:00:02Z' })).kind).toBe('failed');
    }
  });

  it('stops a forward watch on a cancellation, which will never show the step', () => {
    for (const status of ['cancelled', 'out_of_stock', 'declined']) {
      expect(verdict('confirmed', read(status, { cancelled: '2026-10-05T12:00:02Z' }))).toEqual({
        kind: 'failed',
        reason: `The order ended ${status} before it was confirmed.`,
      });
    }
  });

  it('reaches the cancellation only on cancelled, and fails on the saga cancelling or a delivery', () => {
    expect(verdict('cancelled', read('cancelled', { cancelled: '2026-10-05T12:00:02Z' }))).toEqual({
      kind: 'reached',
      at: '2026-10-05T12:00:02Z',
    });
    expect(verdict('cancelled', read('declined')).kind).toBe('failed');
    expect(verdict('cancelled', read('out_of_stock')).kind).toBe('failed');
    expect(verdict('cancelled', read('delivered'))).toEqual({
      kind: 'failed',
      reason: 'The order was delivered: the cancel came too late.',
    });
  });

  it('times a cancellation by asOf when the timeline has not caught up with the status', () => {
    expect(verdict('cancelled', read('cancelled'))).toEqual({
      kind: 'reached',
      at: '2026-10-05T12:00:09Z',
    });
  });

  it('keeps waiting for the cancellation while the order is merely dispatched', () => {
    expect(verdict('cancelled', read('dispatched', { dispatched: '2026-10-05T12:00:02Z' }))).toEqual({
      kind: 'waiting',
    });
  });
});

describe('verdict for the saga refusals', () => {
  it.each(['declined', 'out_of_stock'] as const)('reaches %s only on that status, at the cancel time', (key) => {
    expect(verdict(key, read('placed'))).toEqual({ kind: 'waiting' });
    expect(verdict(key, read(key, { cancelled: '2026-10-05T12:00:01Z' }))).toEqual({
      kind: 'reached',
      at: '2026-10-05T12:00:01Z',
    });
  });

  it('fails a refusal watch on any other end, naming it, rather than waiting out the cap', () => {
    expect(verdict('declined', read('out_of_stock'))).toEqual({
      kind: 'failed',
      reason: 'The order ended out_of_stock, not declined.',
    });
    expect(verdict('out_of_stock', read('cancelled'))).toEqual({
      kind: 'failed',
      reason: 'The order ended cancelled, not out_of_stock.',
    });
    expect(verdict('declined', read('delivered'))).toEqual({
      kind: 'failed',
      reason: 'The order ended delivered, not declined.',
    });
  });
});

describe('withField', () => {
  it('sets one field and keeps the rest', () => {
    expect(JSON.parse(withField('{"name":"Walnut desk","amount":19.99}', 'amount', 10.01))).toEqual({
      name: 'Walnut desk',
      amount: 10.01,
    });
  });

  it('returns a body that is not a JSON object as it was', () => {
    expect(withField('not json', 'amount', 1)).toBe('not json');
    expect(withField('[1]', 'amount', 1)).toBe('[1]');
  });
});

describe('the unhappy scripts', () => {
  it('state their cause the way run-locally.md does: a .01 price, and stock below one', () => {
    expect(SCRIPTS.declined.amount).toBe(10.01);
    expect(SCRIPTS.declined.onHand).toBeGreaterThan(0);
    expect(SCRIPTS['out-of-stock'].onHand).toBe(0);
    expect(SCRIPTS['out-of-stock'].amount).toBeUndefined();
  });

  it('resolve the stock write from the catalog for each script that stocks, and none for the one that stops Inventory', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: Object.values(SCENARIO_OPERATIONS).map((id) => op(id)),
    };

    expect(resolveOperations(view, SCRIPTS.declined).operations?.stock?.id).toBe('inventory:SetOnHand');
    expect(resolveOperations(view, SCRIPTS.deliver).operations?.stock?.id).toBe('inventory:SetOnHand');
    expect(resolveOperations(view, SCRIPTS['stock-timeout']).operations?.stock).toBeUndefined();
  });
});

describe('the timeout scripts', () => {
  it('take their lease first and end it last, and send nothing as the lease or the restore', () => {
    for (const key of ['stock-timeout', 'payment-timeout', 'despatch-timeout', 'release-timeout'] as const) {
      const steps = SCRIPTS[key].steps;

      expect(steps[0]).toBe('lease');
      expect(steps[steps.length - 1]).toBe('restore');
      expect(SCRIPTS[key].lease).toBeDefined();
    }
    expect(roleOf('lease')).toBeNull();
    expect(roleOf('restore')).toBeNull();
    expect(isWatch('lease')).toBe(false);
  });

  it('state their causes as run-locally.md does: Inventory stopped, or a total ending .09, under the short waits', () => {
    expect(SCRIPTS['stock-timeout'].lease!.changes).toEqual(['short-waits', 'stop-inventory']);
    expect(SCRIPTS['payment-timeout'].lease!.changes).toEqual(['short-waits']);
    expect(SCRIPTS['payment-timeout'].amount).toBe(10.09);
    expect(SCRIPTS['payment-timeout'].onHand).toBeGreaterThan(0);
    expect(SCRIPTS['despatch-timeout'].lease!.changes).toEqual(['short-waits']);
  });

  it('hold the stack no longer than the host allows, which is its own bound', () => {
    for (const key of ['stock-timeout', 'payment-timeout', 'despatch-timeout', 'release-timeout'] as const) {
      expect(SCRIPTS[key].lease!.holdSeconds).toBeLessThanOrEqual(600);
    }
  });

  it('resolve only the calls they send, and a lease asks the catalog for none', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: Object.values(SCENARIO_OPERATIONS).map((id) => op(id)),
    };

    const stock = resolveOperations(view, SCRIPTS['stock-timeout']).operations!;

    expect(Object.keys(stock).sort()).toEqual(['order', 'publish', 'read', 'saga']);
  });
});

describe('the release-timeout script', () => {
  it('holds the payment with a .09 total, takes the short waits with the lease and Inventory only after the reservation', () => {
    const script = SCRIPTS['release-timeout'];

    expect(script.amount).toBe(10.09);
    expect(script.onHand).toBeGreaterThan(0);
    expect(script.lease).toEqual({ changes: ['short-waits'], holdSeconds: 420 });
    expect(script.lateChange).toBe('stop-inventory');
    expect(script.review).toEqual({ reason: 'stock_not_released' });
    expect(script.steps).toEqual([
      'lease',
      'publish',
      'drain',
      'stock',
      'quote',
      'order',
      'reserved',
      'stop',
      'cancel',
      'cancelled',
      'flagged',
      'mail',
      'restore',
    ]);
    expect(script.steps.indexOf('reserved')).toBeLessThan(script.steps.indexOf('stop'));
    expect(script.steps.indexOf('stop')).toBeLessThan(script.steps.indexOf('cancel'));
  });

  it('resolves the reservation read for its reserved step, and asks the catalog for nothing on the stop', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: Object.values(SCENARIO_OPERATIONS).map((id) => op(id)),
    };

    expect(SCENARIO_OPERATIONS.reservation).toBe('inventory:GetReservation');
    expect(roleOf('reserved')).toBe('reservation');
    expect(roleOf('stop')).toBeNull();
    expect(isWatch('reserved')).toBe(false);
    expect(isWatch('stop')).toBe(false);
    expect(Object.keys(resolveOperations(view, SCRIPTS['release-timeout']).operations!).sort()).toEqual([
      'cancel',
      'order',
      'publish',
      'quote',
      'read',
      'reservation',
      'saga',
      'stock',
    ]);
  });

  it('names the reservation read when the catalog does not carry it, so the run does not start', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: Object.values(SCENARIO_OPERATIONS)
        .filter((id) => id !== 'inventory:GetReservation')
        .map((id) => op(id)),
    };

    const resolved = resolveOperations(view, SCRIPTS['release-timeout']);

    expect(resolved.operations).toBeNull();
    expect(resolved.missing).toEqual(['inventory:GetReservation is not in the operation catalog']);
  });

  describe('reservationVerdict', () => {
    const reservation = (status: string) => JSON.stringify({ orderId: 'o', status, lines: [] });

    it('reaches Reserved on a 200 that says so', () => {
      expect(reservationVerdict(200, reservation('Reserved'))).toEqual({
        kind: 'reached',
        detail: 'The reservation reads Reserved: Inventory holds the stock for the order.',
      });
    });

    it('waits through a 404, which is a reservation not yet made', () => {
      expect(reservationVerdict(404, '{"code":"reservation.not_found"}')).toEqual({
        kind: 'waiting',
        detail: 'Inventory answered 404: it has made no reservation for the order yet.',
      });
    });

    it('waits through a reservation that is not Reserved yet, naming what it read', () => {
      expect(reservationVerdict(200, reservation('Pending'))).toEqual({
        kind: 'waiting',
        detail: 'Last read: the reservation is Pending, not Reserved yet.',
      });
    });

    it('fails on any other status, since waiting cannot change it', () => {
      for (const status of [401, 403, 500, 503]) {
        expect(reservationVerdict(status, '')).toEqual({ kind: 'failed', reason: `Answered ${status}.` });
      }
    });

    it('fails on a 200 that is not a reservation, rather than waiting on it', () => {
      for (const body of ['', 'not json', '[1]', '"Reserved"', '{}', '{"status":7}']) {
        expect(reservationVerdict(200, body)).toEqual({
          kind: 'failed',
          reason: 'Answered 200, but not with a reservation the step can follow.',
        });
      }
    });
  });
});

describe('the despatch-timeout script', () => {
  it('orders to SIM-STRANGE under the short waits alone, stocks first, and reads the review before the order', () => {
    const script = SCRIPTS['despatch-timeout'];

    expect(script.postalCode).toBe('SIM-STRANGE');
    expect(script.lease).toEqual({ changes: ['short-waits'], holdSeconds: 420 });
    expect(script.onHand).toBeGreaterThan(0);
    expect(script.review).toEqual({ reason: 'not_despatched' });
    expect(script.steps).toEqual([
      'lease',
      'publish',
      'drain',
      'stock',
      'quote',
      'order',
      'confirmed',
      'flagged',
      'held',
      'mail',
      'restore',
    ]);
  });

  it("sends nothing as a step of its own for the review read, which is the host's, so the catalog is asked for no more than the order scripts need", () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: Object.values(SCENARIO_OPERATIONS).map((id) => op(id)),
    };

    expect(roleOf('flagged')).toBeNull();
    expect(isWatch('flagged')).toBe(false);
    expect(Object.keys(resolveOperations(view, SCRIPTS['despatch-timeout']).operations!).sort()).toEqual([
      'order',
      'publish',
      'quote',
      'read',
      'saga',
      'stock',
    ]);
  });

  describe('reviewVerdict', () => {
    const placed = Date.parse('2026-10-05T12:00:00Z');
    const view = (flags: ReviewFlagView['flags'], rest: Partial<ReviewFlagView> = {}): ReviewFlagView => ({
      orderId: 'o1',
      window: '30m',
      reachable: true,
      error: null,
      flags,
      ...rest,
    });
    const flag = (reason: string, at = '2026-10-05T12:01:02.000Z') => ({
      at,
      reason,
      line: `Order o1 flagged for review: ${reason}.`,
    });

    it('is reached by the line for the reason, saying when it was logged and how long after the place', () => {
      expect(reviewVerdict(view([flag('not_despatched')]), 'not_despatched', placed)).toEqual({
        kind: 'reached',
        detail:
          'Ordering logged "Order o1 flagged for review: not_despatched." at 2026-10-05T12:01:02.000Z, 62 s after the order was placed.',
      });
    });

    it('does not claim a time since the place when the place is not known', () => {
      expect(reviewVerdict(view([flag('not_despatched')]), 'not_despatched', 0)).toEqual({
        kind: 'reached',
        detail: 'Ordering logged "Order o1 flagged for review: not_despatched." at 2026-10-05T12:01:02.000Z.',
      });
    });

    it('is reached by the right reason among others, and by the first of a redelivered line', () => {
      const flags = [
        flag('stock_not_released', '2026-10-05T12:00:40.000Z'),
        flag('not_despatched'),
        flag('not_despatched', '2026-10-05T12:03:00.000Z'),
      ];

      const v = reviewVerdict(view(flags), 'not_despatched', placed);

      expect(v.kind).toBe('reached');
      expect((v as { detail: string }).detail).toContain('12:01:02');
    });

    it('waits when nothing is logged yet, and says so', () => {
      expect(reviewVerdict(view([]), 'not_despatched', placed)).toEqual({
        kind: 'waiting',
        detail: 'Ordering has not logged this order as flagged for review yet.',
      });
    });

    it('waits, and says what else it saw, when the order was flagged for another reason only', () => {
      expect(
        reviewVerdict(
          view([flag('stock_not_released'), flag('not_confirmed'), flag('not_confirmed')]),
          'not_despatched',
          placed,
        ),
      ).toEqual({
        kind: 'waiting',
        detail:
          'Ordering has flagged it for review as stock_not_released, not_confirmed, not not_despatched; still waiting for not_despatched.',
      });
    });

    it('fails only when Loki could not be asked, with what it said or that it said nothing', () => {
      expect(
        reviewVerdict(view([], { reachable: false, error: 'Loki answered 502' }), 'not_despatched', placed),
      ).toEqual({
        kind: 'failed',
        reason: 'Loki could not be read: Loki answered 502.',
      });
      expect(reviewVerdict(view([], { reachable: false }), 'not_despatched', placed)).toEqual({
        kind: 'failed',
        reason: 'Loki could not be read: no reason given.',
      });
    });
  });

  it("judges the held read by the script's own cause, not by a carrier outcome", () => {
    const read: OrderRead = {
      status: 'dispatched',
      timeline: {
        placed: null,
        confirmed: '2026-10-05T12:00:01Z',
        dispatched: '2026-10-05T12:00:06Z',
        delivered: null,
        cancelled: null,
      },
      asOf: '2026-10-05T12:00:09Z',
    };

    expect(verdict('held', read)).toEqual({
      kind: 'failed',
      reason: "The order reads dispatched, not confirmed: this script's cause should leave it there.",
    });
  });
});

describe('the carrier log scripts', () => {
  const placed = Date.parse('2026-10-05T12:00:00Z');
  const entry = (
    secondsAfter: number,
    status: number,
    shipmentId: string | null = 's1',
    method = 'POST',
  ): CarrierEntry => ({
    at: placed + secondsAfter * 1000,
    method,
    path: method === 'POST' ? '/v1/shipments' : '/v1/shipments/crr_SIM-STRANGE-o1/events',
    status,
    shipmentId,
  });

  it('place the order to the code they read the log for, and read the order back held at confirmed', () => {
    const codes = {
      'carrier-refused': 'SIM-REFUSED',
      'carrier-down': 'SIM-DOWN',
      'carrier-slow': 'SIM-SLOW',
      'carrier-strange': 'SIM-STRANGE',
    } as const;

    for (const [key, code] of Object.entries(codes) as [keyof typeof codes, string][]) {
      expect(SCRIPTS[key].postalCode).toBe(code);
      expect(SCRIPTS[key].carrier!.code).toBe(code);
      expect(SCRIPTS[key].steps.slice(-4)).toEqual(['confirmed', 'carrier', 'held', 'mail']);
    }
  });

  it('resolve the booking filter for every one and the feed filter for the strange feed alone', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: Object.values(SCENARIO_OPERATIONS).map((id) => op(id)),
    };

    expect(Object.keys(resolveOperations(view, SCRIPTS['carrier-down']).operations!)).toContain('bookings');
    expect(Object.keys(resolveOperations(view, SCRIPTS['carrier-down']).operations!)).not.toContain('polls');
    expect(Object.keys(resolveOperations(view, SCRIPTS['carrier-strange']).operations!)).toContain('polls');
    expect(roleOf('carrier')).toBe('bookings');
    expect(isWatch('carrier')).toBe(false);
  });

  it('set the code in the booking filter and leave any other body as it was', () => {
    const filter = '{"Methods":["POST"],"Body":{"Matchers":[{"Name":"WildcardMatcher","Pattern":"*SIM-DOWN*"}]}}';

    expect(JSON.parse(withCarrierCode(filter, 'SIM-SLOW')).Body.Matchers[0].Pattern).toBe('*SIM-SLOW*');
    expect(JSON.parse(withCarrierCode(filter, 'SIM-SLOW')).Methods).toEqual(['POST']);
    expect(withCarrierCode('{"Methods":["GET"]}', 'SIM-SLOW')).toBe('{"Methods":["GET"]}');
    expect(withCarrierCode('not json', 'SIM-SLOW')).toBe('not json');
  });

  it('ask for the bookings of one run only when the address carries its mark', () => {
    const filter = '{"Methods":["POST"],"Body":{"Matchers":[{"Name":"WildcardMatcher","Pattern":"*SIM-DOWN*"}]}}';
    const marked = JSON.parse(withCarrierCode(filter, 'SIM-SLOW', 'scenario-ab12cd34'));

    expect(marked.Body.MatchOperator).toBe('And');
    expect(marked.Body.Matchers).toEqual([
      { Name: 'WildcardMatcher', Pattern: '*SIM-SLOW*' },
      { Name: 'WildcardMatcher', Pattern: '*scenario-ab12cd34*' },
    ]);
    expect(JSON.parse(withCarrierCode(filter, 'SIM-SLOW')).Body.MatchOperator).toBeUndefined();
    expect(withCarrierCode('{"Methods":["GET"]}', 'SIM-SLOW', 'scenario-ab12cd34')).toBe('{"Methods":["GET"]}');
  });

  it("put the order in the feed filter's path and leave any other body as it was", () => {
    const filter =
      '{"Methods":["GET"],"Path":{"Matchers":[{"Name":"ExactMatcher","Pattern":"/v1/shipments/crr_SIM-STRANGE-{orderId}/events"}]}}';

    expect(JSON.parse(withFeedOrder(filter, 'ord-1')).Path.Matchers[0].Pattern).toBe(
      '/v1/shipments/crr_SIM-STRANGE-ord-1/events',
    );
    expect(JSON.parse(withFeedOrder(filter, 'ord-1')).Methods).toEqual(['GET']);
    expect(JSON.parse(withFeedOrder(filter, 'a$&b')).Path.Matchers[0].Pattern).toBe(
      '/v1/shipments/crr_SIM-STRANGE-a$&b/events',
    );
    expect(withFeedOrder('{"Methods":["GET"]}', 'ord-1')).toBe('{"Methods":["GET"]}');
    expect(withFeedOrder('not json', 'ord-1')).toBe('not json');
  });

  it("read the simulator's entries, and nothing that is not a list of them", () => {
    const body = JSON.stringify([
      {
        Request: {
          DateTime: '2026-10-05T12:00:03Z',
          Method: 'POST',
          Path: '/v1/shipments',
          Body: '{"shipmentId":"s1"}',
        },
        Response: { StatusCode: 503 },
      },
      {
        Request: { DateTime: '2026-10-05T12:00:04Z', Method: 'GET', Path: '/v1/shipments/x/events', Body: null },
        Response: { StatusCode: 200 },
      },
    ]);

    expect(carrierEntries(body)).toEqual([
      { at: Date.parse('2026-10-05T12:00:03Z'), method: 'POST', path: '/v1/shipments', status: 503, shipmentId: 's1' },
      {
        at: Date.parse('2026-10-05T12:00:04Z'),
        method: 'GET',
        path: '/v1/shipments/x/events',
        status: 200,
        shipmentId: null,
      },
    ]);
    expect(carrierEntries('[]')).toEqual([]);
    expect(carrierEntries('{"Status":"Bad JSON escape sequence"}')).toBeNull();
    expect(carrierEntries('[{"Request":{"DateTime":"nope"},"Response":{"StatusCode":200}}]')).toBeNull();
    expect(carrierEntries('[{"Request":{')).toBeNull();
  });

  it('wait for a booking since the order was placed, and ignore one logged before it', () => {
    expect(carrierVerdict('refused', [], [], placed).kind).toBe('waiting');
    expect(carrierVerdict('refused', [entry(-60, 422)], [], placed).kind).toBe('waiting');
  });

  it('read a refusal as one booking answered 422, and fail any other answer', () => {
    expect(carrierVerdict('refused', [entry(2, 422)], [], placed).kind).toBe('reached');
    expect(carrierVerdict('refused', [entry(2, 201)], [], placed)).toEqual({
      kind: 'failed',
      reason: 'The carrier answered 201 to a booking attempt, not 422.',
    });
  });

  it('read an outage as repeated attempts at one booking each answered 503, and wait for the second', () => {
    expect(carrierVerdict('down', [entry(2, 503)], [], placed).kind).toBe('waiting');
    expect(carrierVerdict('down', [entry(2, 503), entry(3, 503)], [], placed).kind).toBe('reached');
    expect(carrierVerdict('down', [entry(2, 503), entry(3, 201)], [], placed).kind).toBe('failed');
  });

  it("count only the attempts of this order's own booking, by its shipment id", () => {
    // A later booking of another shipment is not a retry of this one.
    expect(carrierVerdict('down', [entry(2, 503, 'a'), entry(9, 503, 'b')], [], placed).kind).toBe('waiting');
    expect(carrierVerdict('down', [entry(2, 503, 'a'), entry(9, 503, 'b'), entry(10, 503, 'a')], [], placed).kind).toBe(
      'reached',
    );
  });

  it('read a slow carrier as repeated 201s of one booking', () => {
    expect(carrierVerdict('slow', [entry(2, 201)], [], placed).kind).toBe('waiting');
    expect(carrierVerdict('slow', [entry(2, 201), entry(4, 201)], [], placed).kind).toBe('reached');
    expect(carrierVerdict('slow', [entry(2, 503), entry(4, 503)], [], placed).kind).toBe('failed');
  });

  it('read a strange feed as one booking and then at least two polls of the feed since it', () => {
    const polls = (...seconds: number[]) => seconds.map((s) => entry(s, 200, null, 'GET'));

    expect(carrierVerdict('strange', [entry(2, 201)], [], placed).kind).toBe('waiting');
    expect(carrierVerdict('strange', [entry(2, 201)], polls(5), placed).kind).toBe('waiting');
    expect(carrierVerdict('strange', [entry(2, 201)], polls(5, 8), placed).kind).toBe('reached');
    // A poll before the booking is another parcel's.
    expect(carrierVerdict('strange', [entry(2, 201)], polls(-10, 1, 5), placed).kind).toBe('waiting');
    expect(carrierVerdict('strange', [entry(2, 422)], polls(5, 8), placed).kind).toBe('failed');
  });

  it('hold the order read at confirmed and nothing past it', () => {
    const read = (status: string, dispatched: string | null): OrderRead => ({
      status,
      timeline: {
        placed: '2026-10-05T12:00:00Z',
        confirmed: '2026-10-05T12:00:01Z',
        dispatched,
        delivered: null,
        cancelled: null,
      },
      asOf: '2026-10-05T12:00:09Z',
    });

    expect(verdict('held', read('confirmed', null))).toEqual({ kind: 'reached', at: '2026-10-05T12:00:01Z' });
    expect(verdict('held', read('dispatched', '2026-10-05T12:00:06Z')).kind).toBe('failed');
    expect(verdict('held', read('cancelled', null)).kind).toBe('failed');
  });
});

describe('the carrier scripts', () => {
  const order = JSON.stringify({
    items: [{ productId: 'p' }],
    shippingAddress: { line1: '1 Test Street', postalCode: '050000' },
  });

  it('state their cause the way run-locally.md does: the SIM-TRANSIT postal code', () => {
    expect(SCRIPTS['in-transit'].postalCode).toBe('SIM-TRANSIT');
    expect(SCRIPTS['already-shipped'].postalCode).toBe('SIM-TRANSIT');
    expect(SCRIPTS.deliver.postalCode).toBeUndefined();
  });

  it('set the postal code on the address and keep the rest of the order', () => {
    const body = JSON.parse(withShippingPostalCode(order, 'SIM-TRANSIT'));

    expect(body.shippingAddress).toEqual({ line1: '1 Test Street', postalCode: 'SIM-TRANSIT' });
    expect(body.items).toEqual([{ productId: 'p' }]);
    expect(hasShippingPostalCode(withShippingPostalCode(order, 'SIM-TRANSIT'), 'SIM-TRANSIT')).toBe(true);
  });

  it('leave an order with no address, or no JSON, as it was', () => {
    expect(JSON.parse(withShippingPostalCode(order, 'SIM-DOWN', 'scenario-ab12cd34')).shippingAddress.line1).toBe(
      `${JSON.parse(order).shippingAddress.line1} scenario-ab12cd34`,
    );
    expect(withShippingPostalCode('{"items":[]}', 'SIM-TRANSIT')).toBe('{"items":[]}');
    expect(withShippingPostalCode('not json', 'SIM-TRANSIT')).toBe('not json');
    expect(hasShippingPostalCode('{"items":[]}', 'SIM-TRANSIT')).toBe(false);
  });

  it('send the late cancel as the cancel operation, and read the order back as the order read', () => {
    expect(roleOf('late_cancel')).toBe('cancel');
    expect(roleOf('unchanged')).toBe('read');
    expect(isWatch('unchanged')).toBe(true);
    expect(isWatch('late_cancel')).toBe(false);
  });

  it('count only the platforms 422 with its code as the refusal of a cancel after despatch', () => {
    expect(isAlreadyShipped(422, '{"code":"order.already_shipped"}')).toBe(true);
    expect(isAlreadyShipped(422, '{"code":"order.not_found"}')).toBe(false);
    expect(isAlreadyShipped(204, '')).toBe(false);
    expect(isAlreadyShipped(422, 'not json')).toBe(false);
    expect(isAlreadyShipped(409, '{"code":"order.already_shipped"}')).toBe(false);
  });

  it('reads an unchanged order only as still despatched and never cancelled', () => {
    const at = '2026-10-05T12:00:02Z';
    const read = (status: string, cancelled: string | null): OrderRead => ({
      status,
      timeline: {
        placed: '2026-10-05T12:00:00Z',
        confirmed: '2026-10-05T12:00:01Z',
        dispatched: at,
        delivered: null,
        cancelled,
      },
      asOf: at,
    });

    expect(verdict('unchanged', read('dispatched', null))).toEqual({ kind: 'reached', at });
    expect(verdict('unchanged', read('cancelled', at)).kind).toBe('failed');
    expect(verdict('unchanged', read('delivered', null)).kind).toBe('failed');
  });
});

describe('the image steps', () => {
  it('send as the signing and the product read, and sit in a script of their own', () => {
    expect(roleOf('upload')).toBe('image');
    expect(roleOf('thumbnail')).toBe('product');
    expect(SCRIPTS.image.steps).toEqual(['upload', 'publish', 'thumbnail']);
    expect(SCRIPTS.deliver.steps).not.toContain('upload');
    expect(SCENARIO_OPERATIONS.image).toBe('bff:IssueProductImageUpload');
    expect(SCENARIO_OPERATIONS.product).toBe('catalog:GetProduct');
  });

  it('reads the upload the BFF issued, and nothing that is not one', () => {
    const body =
      '{"uploadUrl":"http://127.0.0.1:8333/b/k?X-Amz-Signature=s","headers":{"Content-Type":"image/png","n":1},"key":"uploads/a/b.png","expiresAt":"x"}';

    expect(uploadFrom(body)).toEqual({
      uploadUrl: 'http://127.0.0.1:8333/b/k?X-Amz-Signature=s',
      headers: { 'Content-Type': 'image/png' },
      key: 'uploads/a/b.png',
    });
    for (const bad of [
      '',
      'nope',
      '[]',
      '"x"',
      '{}',
      '{"uploadUrl":"u"}',
      '{"key":"k"}',
      '{"uploadUrl":"","key":"k"}',
    ]) {
      expect(uploadFrom(bad), bad).toBeNull();
    }
    expect(uploadFrom('{"uploadUrl":"u","key":"k"}')?.headers).toEqual({});
  });

  it('reads a thumbnail only where the product has one', () => {
    expect(thumbnailFrom('{"thumbnailUrl":"http://e/p.png"}')).toBe('http://e/p.png');
    for (const none of ['', 'nope', '[]', '{}', '{"thumbnailUrl":null}', '{"thumbnailUrl":""}', '{"thumbnailUrl":3}']) {
      expect(thumbnailFrom(none), none).toBeNull();
    }
  });

  it('finds a header without regard to case, and judges an image by status, type and nosniff', () => {
    const ok = { 'content-type': ['image/webp'], 'X-CONTENT-TYPE-OPTIONS': ['NoSniff'] };

    expect(headerOf(ok, 'Content-Type')).toBe('image/webp');
    expect(headerOf(ok, 'ETag')).toBeNull();
    expect(imageFault(200, ok)).toBeNull();
    expect(imageFault(204, ok)).toBeNull();
    expect(imageFault(301, ok)).toBe('The edge answered 301, not the image.');
    expect(imageFault(200, { 'Content-Type': ['image/png'] })).toBe(
      'The edge did not set X-Content-Type-Options: nosniff.',
    );
  });

  it('puts a string into a body as it puts a number', () => {
    expect(JSON.parse(withField('{"name":"x"}', 'imageKey', 'uploads/a/b.png'))).toEqual({
      name: 'x',
      imageKey: 'uploads/a/b.png',
    });
  });
});

describe('secondsBetween', () => {
  it('measures to a tenth of a second, and gives nothing for a missing or unreadable instant', () => {
    expect(secondsBetween('2026-10-05T12:00:00Z', '2026-10-05T12:00:03.25Z')).toBe(3.3);
    expect(secondsBetween(null, '2026-10-05T12:00:03Z')).toBeNull();
    expect(secondsBetween('nope', '2026-10-05T12:00:03Z')).toBeNull();
  });
});

describe('hasField', () => {
  it('is true only for a JSON object carrying the field at that value', () => {
    expect(hasField('{"onHand":0}', 'onHand', 0)).toBe(true);
    expect(hasField('{"onHand":3}', 'onHand', 0)).toBe(false);
    expect(hasField('{}', 'onHand', 0)).toBe(false);
    expect(hasField('[0]', 'onHand', 0)).toBe(false);
    expect(hasField('not json', 'onHand', 0)).toBe(false);
  });
});

describe('sagaStrip', () => {
  const none = { placed: null, confirmed: null, dispatched: null, delivered: null, cancelled: null };

  function read(
    status: string,
    readAt: number,
    timeline: Partial<SagaRead['timeline']> = {},
    saga: SagaRead['saga'] = null,
  ): SagaRead {
    return {
      readAt,
      status,
      asOf: '2026-10-05T12:00:09Z',
      timeline: { ...none, placed: '2026-10-05T12:00:00Z', ...timeline },
      saga,
    };
  }

  const row = (state: string): SagaWord => ({
    kind: 'row',
    state,
    startedAt: '2026-10-05T12:00:00Z',
    cancelReason: null,
  });
  const noRow: SagaWord = { kind: 'none' };

  it('has nothing to show before the first read', () => {
    expect(sagaStrip([])).toBeNull();
  });

  it("shows the last read, and the time each transition took from the platform's two stamps", () => {
    const strip = sagaStrip([
      read('placed', 1_000),
      read('delivered', 9_000, {
        confirmed: '2026-10-05T12:00:01Z',
        dispatched: '2026-10-05T12:00:06Z',
        delivered: '2026-10-05T12:00:40Z',
      }),
    ])!;

    expect(strip.status).toBe('delivered');
    expect(strip.readAt).toBe(9_000);
    expect(strip.reads).toBe(2);
    expect(strip.transitions).toEqual([
      { from: 'placed', to: 'confirmed', seconds: 1 },
      { from: 'confirmed', to: 'dispatched', seconds: 5 },
      { from: 'dispatched', to: 'delivered', seconds: 34 },
    ]);
  });

  it('names a cancellation as a transition from the last step before it', () => {
    const strip = sagaStrip([read('cancelled', 1_000, { cancelled: '2026-10-05T12:00:02Z' })])!;

    expect(strip.transitions).toEqual([{ from: 'placed', to: 'cancelled', seconds: 2 }]);
  });

  it('stalls a status that has stood still for the whole stall time, and not before', () => {
    const early = sagaStrip([read('confirmed', 0), read('confirmed', STALL_AFTER_MS - 1)])!;
    const late = sagaStrip([read('confirmed', 0), read('confirmed', 10_000), read('confirmed', STALL_AFTER_MS)])!;

    expect(early.stalled).toBe(false);
    expect(late.stalled).toBe(true);
    expect(late.unchangedForMs).toBe(STALL_AFTER_MS);
  });

  it('counts the stand-still from the last change, not from the first read', () => {
    const strip = sagaStrip([
      read('placed', 0),
      read('confirmed', STALL_AFTER_MS),
      read('confirmed', STALL_AFTER_MS + 1_000),
    ])!;

    expect(strip.unchangedForMs).toBe(1_000);
    expect(strip.stalled).toBe(false);
  });

  it('does not call an end a stall: delivered, cancelled, declined and out of stock are where the saga stops', () => {
    for (const status of ['delivered', 'cancelled', 'declined', 'out_of_stock']) {
      expect(sagaStrip([read(status, 0), read(status, STALL_AFTER_MS * 2)])!.stalled).toBe(false);
    }
  });

  it("carries no second column where the saga was not read, and says when the buyer's status last changed", () => {
    const strip = sagaStrip([read('confirmed', 0, { confirmed: '2026-10-05T12:00:01Z' })])!;

    expect(strip.saga).toBeNull();
    expect(strip.pairing).toBeNull();
    expect(strip.sagaSeenAt).toBeNull();
    expect(strip.statusChangedAt).toBe('2026-10-05T12:00:01Z');
  });

  it("reads a refusal's time from the cancellation stamp, and an unknown status as having none", () => {
    expect(sagaStrip([read('declined', 0, { cancelled: '2026-10-05T12:00:03Z' })])!.statusChangedAt).toBe(
      '2026-10-05T12:00:03Z',
    );
    expect(sagaStrip([read('on_the_moon', 0)])!.statusChangedAt).toBeNull();
  });

  it('pairs the last reads and counts the saga as seen in its state from the first trailing read that shows it', () => {
    const strip = sagaStrip([
      read('placed', 100, {}, row('AwaitingConfirmation')),
      read('confirmed', 2_000, {}, row('Confirmed')),
      read('confirmed', 4_000, {}, row('Confirmed')),
    ])!;

    expect(strip.saga).toEqual(row('Confirmed'));
    expect(strip.pairing).toBe('agree');
    expect(strip.sagaSeenAt).toBe(2_000);
  });

  it('claims no first sighting for a saga read that gave no state', () => {
    const strip = sagaStrip([read('confirmed', 0, {}, { kind: 'unreadable', status: 403 })])!;

    expect(strip.saga).toEqual({ kind: 'unreadable', status: 403 });
    expect(strip.sagaSeenAt).toBeNull();
  });

  it('shows a disagreement in fact as one, and the buyer running ahead of the saga as that', () => {
    expect(sagaStrip([read('confirmed', 0, {}, row('Compensating'))])!.pairing).toBe('agree');
    expect(sagaStrip([read('delivered', 0, {}, row('AwaitingStock'))])!.pairing).toBe('differs');
    expect(sagaStrip([read('dispatched', 0, {}, row('Confirmed'))])!.pairing).toBe('ahead');
    expect(sagaStrip([read('confirmed', 0, {}, noRow)])!.pairing).toBe('ahead');
  });
});

describe('sagaWord', () => {
  const body = JSON.stringify({
    orderId: '0199b0c4-8b21-7c53-8e7f-4a6b8c9d0e1f',
    currentState: 'Confirmed',
    startedAt: '2026-10-05T12:00:00Z',
    cancelReason: null,
  });

  it("reads Ordering's SagaStateView, a compensation's reason included", () => {
    expect(sagaWord(200, body)).toEqual({
      kind: 'row',
      state: 'Confirmed',
      startedAt: '2026-10-05T12:00:00Z',
      cancelReason: null,
    });
    expect(sagaWord(200, body.replace('null', '"payment_declined"'))).toMatchObject({
      cancelReason: 'payment_declined',
    });
  });

  it('takes a 404 for no row, and anything else that is not a readable state for unreadable', () => {
    expect(sagaWord(404, '')).toEqual({ kind: 'none' });
    expect(sagaWord(403, '')).toEqual({ kind: 'unreadable', status: 403 });
    expect(sagaWord(200, 'not json')).toEqual({ kind: 'unreadable', status: 200 });
    expect(sagaWord(200, '[]')).toEqual({ kind: 'unreadable', status: 200 });
    expect(sagaWord(200, '{"currentState":1,"startedAt":"x"}')).toEqual({ kind: 'unreadable', status: 200 });
    expect(sagaWord(200, '{"currentState":"Confirmed","startedAt":"x","cancelReason":5}')).toEqual({
      kind: 'unreadable',
      status: 200,
    });
  });
});

describe('pairing', () => {
  const row = (state: string): SagaWord => ({ kind: 'row', state, startedAt: 'x', cancelReason: null });

  it('claims nothing for a read that gave no state, or a state the backend does not name', () => {
    expect(pairing(null, 'placed')).toBe('unknown');
    expect(pairing({ kind: 'unreadable', status: 403 }, 'placed')).toBe('unknown');
    expect(pairing(row('Nonesuch'), 'placed')).toBe('unknown');
  });

  it("holds the backend's table: a row's own statuses agree, its listed ones are ahead, the rest differ", () => {
    expect(pairing(row('AwaitingPayment'), 'placed')).toBe('agree');
    expect(pairing(row('AwaitingPayment'), 'cancelled')).toBe('ahead');
    expect(pairing(row('AwaitingPayment'), 'confirmed')).toBe('differs');
    expect(pairing(row('AwaitingConfirmation'), 'dispatched')).toBe('ahead');
    expect(pairing(row('Compensating'), 'declined')).toBe('ahead');
    expect(pairing(row('Compensating'), 'delivered')).toBe('differs');
    expect(pairing({ kind: 'none' }, 'delivered')).toBe('agree');
    expect(pairing({ kind: 'none' }, 'out_of_stock')).toBe('agree');
  });

  it('puts words to each pairing', () => {
    for (const p of ['agree', 'ahead', 'differs', 'unknown'] as const) expect(pairingText(p)).not.toBe('');
    expect(sagaText({ kind: 'row', state: 'Confirmed', startedAt: 'x', cancelReason: null })).toBe('Confirmed');
    expect(sagaText({ kind: 'none' })).toContain('no row');
    expect(sagaText({ kind: 'unreadable', status: 403 })).toContain('403');
    expect(sagaText({ kind: 'unreadable', status: null })).toContain('no answer');
  });
});

describe('the mail count', () => {
  const EXPECTED = ['order-placed', 'order-confirmed'];
  const EVENT = 'f'.repeat(32);

  function mail(messages: [string, string | null][], over: Partial<MailListView> = {}): MailListView {
    return {
      reachable: true,
      error: null,
      total: messages.length,
      sinkUrl: 'http://localhost:8025',
      messages: messages.map(([messageId, correlationId], i) => ({
        id: `m${i}`,
        messageId,
        from: { name: '', address: 'no-reply@commerce.test' },
        to: [],
        subject: '',
        created: '2026-10-15T12:00:00Z',
        snippet: '',
        read: false,
        correlationId,
      })),
      ...over,
    };
  }

  const ORDER = '01a14a4e-0d03-7adf-b1f6-2ba62a99f00a';
  const OTHER_ORDER = '01a14a4f-16d8-7a5c-9d1e-3b0a8c2f4e11';
  const placed = (event = EVENT): [string, string] => [`${event}.order-placed@commerce.test`, ORDER];
  const confirmed = (event = '1'.repeat(32)): [string, string] => [`${event}.order-confirmed@commerce.test`, ORDER];

  it('names the event a template answers, and a key it does not know by itself', () => {
    expect(mailEvent('shipment-delivered')).toBe('ShipmentDelivered');
    expect(mailEvent('newsletter')).toBe('newsletter');
  });

  it("counts the order's messages by the event in each Message-ID, and not another order's", () => {
    const tally = tallyMail(
      mail([
        ['x.order-placed@commerce.test', OTHER_ORDER],
        ['y.order-placed@commerce.test', null],
        confirmed(),
        placed(),
        placed(),
      ]),
      ORDER,
    );

    expect(tally.map((t) => [t.event, t.messages])).toEqual([
      ['OrderPlaced', 2],
      ['OrderConfirmed', 1],
    ]);
  });

  it('counts a Message-ID it cannot read under the whole id, with no event', () => {
    expect(tallyMail(mail([['odd@commerce.test', ORDER]]), ORDER)).toEqual([
      { event: '', template: '', eventId: 'odd', messages: 1 },
    ]);
  });

  it('reaches on one message for each expected event, saying the count of each', () => {
    const v = mailVerdict(mail([confirmed(), placed()]), ORDER, EXPECTED);

    expect(v).toEqual({
      kind: 'reached',
      detail:
        'One message for each event the order raised: OrderPlaced (order-placed) ×1, OrderConfirmed (order-confirmed) ×1.',
    });
  });

  it('waits, naming what is missing, while the send worker has not sent it', () => {
    expect(mailVerdict(mail([placed()]), ORDER, EXPECTED)).toEqual({
      kind: 'waiting',
      detail: 'Not sent yet: OrderConfirmed (order-confirmed). Counted: OrderPlaced (order-placed) ×1.',
    });
    expect(mailVerdict(mail([]), ORDER, EXPECTED)).toMatchObject({
      kind: 'waiting',
      detail: expect.stringContaining('no message for this run yet'),
    });
  });

  it('fails a second message for one event as two, which cannot be read as success', () => {
    const v = mailVerdict(mail([confirmed(), placed(), placed()]), ORDER, EXPECTED);

    expect(v).toEqual({
      kind: 'failed',
      reason:
        'A message was sent more than once: OrderPlaced (order-placed) sent 2 times. Counted: OrderPlaced (order-placed) ×2, OrderConfirmed (order-confirmed) ×1.',
    });
  });

  it('fails a message for an event the script does not expect, and a sink that cannot be read', () => {
    const extra: [string, string] = [`${'2'.repeat(32)}.order-cancelled@commerce.test`, ORDER];

    expect(mailVerdict(mail([placed(), confirmed(), extra]), ORDER, EXPECTED)).toMatchObject({
      kind: 'failed',
      reason: expect.stringContaining('does not expect: OrderCancelled (order-cancelled)'),
    });
    expect(mailVerdict(mail([], { reachable: false, error: 'refused' }), ORDER, EXPECTED)).toEqual({
      kind: 'failed',
      reason: 'The mail sink could not be read: refused.',
    });
  });

  it("matches the order's id however the sink spells its case, and never the scenario id the request carried", () => {
    const upper: [string, string] = [`${EVENT}.order-placed@commerce.test`, ORDER.toUpperCase()];
    const carried: [string, string] = [`${EVENT}.order-placed@commerce.test`, 'scenario-r1-order'];

    expect(tallyMail(mail([upper]), ORDER).map((t) => t.messages)).toEqual([1]);
    expect(tallyMail(mail([carried]), ORDER)).toEqual([]);
  });

  it("is asked of the scripts whose order's mail was read live to be the same each run, for the events it raises", () => {
    expect(SCRIPTS.deliver.mail).toEqual([
      'order-placed',
      'order-confirmed',
      'shipment-dispatched',
      'shipment-delivered',
    ]);
    expect(SCRIPTS.cancel.mail).toEqual(['order-placed', 'order-cancelled']);
    expect(SCRIPTS['out-of-stock'].mail).toEqual(['order-placed', 'order-cancelled']);
    expect(SCRIPTS.declined.mail).toEqual(['order-placed', 'order-cancelled', 'payment-declined']);
    for (const key of [
      'carrier-refused',
      'carrier-down',
      'carrier-slow',
      'carrier-strange',
      'despatch-timeout',
    ] as const) {
      expect(SCRIPTS[key].mail).toEqual(['order-placed', 'order-confirmed']);
    }
    for (const key of ['stock-timeout', 'release-timeout'] as const) {
      expect(SCRIPTS[key].mail).toEqual(['order-placed', 'order-cancelled']);
    }
    expect(SCRIPTS['payment-timeout'].mail).toEqual(['order-placed', 'order-cancelled', 'payment-declined']);
    expect(Object.values(SCRIPTS).filter((s) => s.mail).length).toBe(12);
    expect(roleOf('mail')).toBeNull();
  });

  it('puts the mail step last in each script that asks for it, before the restore of one that holds a lease', () => {
    for (const script of Object.values(SCRIPTS).filter((s) => s.mail)) {
      const steps = script.steps.filter((k) => k !== 'restore');
      expect(steps.at(-1)).toBe('mail');
      if (script.lease) expect(script.steps.at(-1)).toBe('restore');
    }
  });
});
