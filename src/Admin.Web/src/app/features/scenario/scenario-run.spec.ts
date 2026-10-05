import { ApiCatalogView, ApiOperation } from '../../core/host/host-types';
import {
  OrderRead,
  SCENARIO_OPERATIONS,
  SCRIPTS,
  idFrom,
  orderRead,
  resolveOperations,
  secondsBetween,
  stepCorrelationId,
  verdict,
  withProductId,
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
      "ordering:PlaceOrder is unavailable: its service's document did not load",
      'ordering:CancelOrder is not in the operation catalog',
    ]);
  });

  it("names the source's own error for an unavailable operation", () => {
    const view: ApiCatalogView = {
      sources: [
        { name: 'ordering', documentUrl: 'u', available: false, error: 'connection refused', changes: null },
      ],
      operations: Object.values(SCENARIO_OPERATIONS).map((id) => op(id, !id.startsWith('ordering'))),
    };

    expect(resolveOperations(view, SCRIPTS.deliver).missing[0]).toBe(
      "ordering:PlaceOrder is unavailable: its service's document did not load: connection refused",
    );
  });
});

describe('idFrom', () => {
  it('reads the bare JSON string a Result<Guid> answers with', () => {
    expect(idFrom('"0199a1b2-0000-7000-8000-000000000001"')).toBe(
      '0199a1b2-0000-7000-8000-000000000001',
    );
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
    const quote = JSON.parse(
      withProductId('{"currency":"EUR","lines":[{"productId":"0","quantity":1}]}', 'p-1'),
    );
    const order = JSON.parse(
      withProductId(
        '{"commandId":"c","items":[{"productId":"0","quantity":1}],"currency":"EUR"}',
        'p-1',
      ),
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
    expect(
      orderRead(JSON.stringify({ status: 'placed', timeline: { ...timeline, placed: 5 }, asOf: 'x' })),
    ).toBeNull();
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

describe('secondsBetween', () => {
  it('measures to a tenth of a second, and gives nothing for a missing or unreadable instant', () => {
    expect(secondsBetween('2026-10-05T12:00:00Z', '2026-10-05T12:00:03.25Z')).toBe(3.3);
    expect(secondsBetween(null, '2026-10-05T12:00:03Z')).toBeNull();
    expect(secondsBetween('nope', '2026-10-05T12:00:03Z')).toBeNull();
  });
});
