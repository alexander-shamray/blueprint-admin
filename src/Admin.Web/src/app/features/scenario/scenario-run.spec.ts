import { ApiCatalogView, ApiOperation } from '../../core/host/host-types';
import {
  OrderRead,
  SCENARIO_OPERATIONS,
  SCRIPTS,
  headerOf,
  idFrom,
  imageFault,
  orderRead,
  resolveOperations,
  roleOf,
  secondsBetween,
  stepCorrelationId,
  thumbnailFrom,
  uploadFrom,
  verdict,
  hasField,
  withField,
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
    // Without asOf a waiting read would say "as the BFF knew it at undefined" for the whole cap.
    expect(orderRead(JSON.stringify({ status: 'placed', timeline }))).toBeNull();
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

  it('resolve the stock write from the catalog beside the calls every script makes', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: Object.values(SCENARIO_OPERATIONS).map((id) => op(id)),
    };

    expect(resolveOperations(view, SCRIPTS.declined).operations?.stock?.id).toBe('inventory:SetOnHand');
    expect(resolveOperations(view, SCRIPTS.deliver).operations?.stock).toBeUndefined();
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
    const body = '{"uploadUrl":"http://127.0.0.1:8333/b/k?X-Amz-Signature=s","headers":{"Content-Type":"image/png","n":1},"key":"uploads/a/b.png","expiresAt":"x"}';

    expect(uploadFrom(body)).toEqual({
      uploadUrl: 'http://127.0.0.1:8333/b/k?X-Amz-Signature=s',
      headers: { 'Content-Type': 'image/png' },
      key: 'uploads/a/b.png',
    });
    for (const bad of ['', 'nope', '[]', '"x"', '{}', '{"uploadUrl":"u"}', '{"key":"k"}', '{"uploadUrl":"","key":"k"}']) {
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
    expect(imageFault(200, { 'Content-Type': ['image/png'] })).toBe('The edge did not set X-Content-Type-Options: nosniff.');
  });

  it('puts a string into a body as it puts a number', () => {
    expect(JSON.parse(withField('{"name":"x"}', 'imageKey', 'uploads/a/b.png'))).toEqual({ name: 'x', imageKey: 'uploads/a/b.png' });
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
