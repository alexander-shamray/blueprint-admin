import { ApiCatalogView, ApiOperation } from '../../core/host/host-types';
import {
  STEP_OPERATIONS,
  idFrom,
  resolveOperations,
  stepCorrelationId,
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
  it('finds every operation the run sends', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: Object.values(STEP_OPERATIONS).map((id) => op(id)),
    };

    const { operations, missing } = resolveOperations(view);

    expect(missing).toEqual([]);
    expect(operations?.cancel.id).toBe('ordering:CancelOrder');
  });

  it('names an operation that is missing and one that is unavailable, and offers no run', () => {
    const view: ApiCatalogView = {
      sources: [],
      operations: [
        op(STEP_OPERATIONS.publish),
        op(STEP_OPERATIONS.quote),
        op(STEP_OPERATIONS.order, false),
      ],
    };

    const { operations, missing } = resolveOperations(view);

    expect(operations).toBeNull();
    expect(missing).toEqual([
      "ordering:PlaceOrder is unavailable: its service's document did not load",
      'ordering:CancelOrder is not in the operation catalog',
    ]);
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
  });
});
