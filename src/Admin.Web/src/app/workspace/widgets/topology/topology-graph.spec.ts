import { PlatformMapView, StackView } from '../../../core/host/host-types';
import { COMPOSE_SERVICES } from '../../../features/logs/compose-services';
import { COMPOSE_OF, composeOf, positionsOf, topologyOf } from './topology-graph';

const map: PlatformMapView = {
  brokerReachable: true,
  error: null,
  services: [
    { name: 'gateway', url: 'http://localhost:5000', routes: [], queues: [] },
    {
      name: 'ordering',
      url: 'http://localhost:5101',
      routes: [{ method: null, prefix: '/api/v1/orders/', policy: 'authenticated' }],
      queues: [
        {
          name: 'ordering-catalog-events',
          consumer: "Ordering's price projection",
          messages: 2,
          parked: null,
          bindings: [{ source: 'Common.Contracts.Catalog.V1:PriceChanged', routingKey: '' }],
        },
        {
          name: 'ordering-catalog-events_error',
          consumer: "Ordering's price projection",
          messages: 1,
          parked: 'error',
          bindings: [],
        },
      ],
    },
    {
      name: 'shipping',
      url: null,
      routes: [],
      queues: [{ name: 'shipping-orders', consumer: 'Shipping', messages: 0, parked: null, bindings: [] }],
    },
    { name: 'catalog', url: 'http://localhost:5100', routes: [], queues: [] },
  ],
  disagreements: [],
};

const stack = {
  backend: {
    reachable: true,
    error: null,
    services: [{ service: 'shipping-worker', state: 'running', health: null, exitCode: null, publishedPorts: [] }],
  },
  reachability: [
    { name: 'gateway', url: 'http://localhost:5000/health/ready', up: true, status: 200 },
    { name: 'ordering', url: 'http://localhost:5101/health/ready', up: false, status: 503 },
    { name: 'catalog', url: 'http://localhost:5100/health/ready', up: false, status: null },
  ],
} as unknown as StackView;

/** `null` is a stack not read yet, which a default parameter could not say. */
const reads = (rates: Record<string, number> = {}, from: StackView | null = stack) => ({
  stack: from ?? undefined,
  rate: (q: string) => rates[q],
  rateText: (q: string) => (rates[q] === undefined ? '—' : `${rates[q]}/s`),
});

function node(id: string, rates?: Record<string, number>, from?: StackView | null) {
  const found = topologyOf(map, reads(rates, from)).nodes.find((n) => n.id === id);
  if (!found) throw new Error(`no node ${id}`);
  return found;
}

describe('topologyOf', () => {
  it('has a node for the gateway, each service, each queue and each exchange, and folds a parked twin into its queue', () => {
    const { nodes } = topologyOf(map, reads());

    expect(nodes.map((n) => `${n.kind} ${n.name}`)).toEqual([
      'gateway gateway',
      'service ordering',
      'service shipping',
      'service catalog',
      'queue ordering-catalog-events',
      'queue shipping-orders',
      'exchange Common.Contracts.Catalog.V1:PriceChanged',
    ]);
  });

  it('draws gateway to service, exchange to queue with the depth and rate, and queue to consumer', () => {
    const { edges } = topologyOf(map, reads({ 'ordering-catalog-events': 0.5 }));

    expect(edges.map((e) => `${e.kind} ${e.source} -> ${e.target} [${e.label}]`)).toEqual([
      'route s:gateway -> s:ordering [/api/v1/orders/]',
      'binding x:Common.Contracts.Catalog.V1:PriceChanged -> q:ordering-catalog-events [2 · 0.5/s]',
      'consume q:ordering-catalog-events -> s:ordering []',
      // Nothing is bound to it, so its depth and rate ride the edge out.
      'consume q:shipping-orders -> s:shipping [0 · —]',
    ]);
  });

  it('labels the route edge with the count when a service has several routes', () => {
    const two = {
      ...map,
      services: map.services.map((s) =>
        s.name === 'ordering'
          ? { ...s, routes: [...s.routes, { method: 'POST', prefix: '/api/v1/orders/place', policy: 'authenticated' }] }
          : s,
      ),
    };

    expect(topologyOf(two, reads()).edges.find((e) => e.id === 'route:ordering')?.label).toBe('2 routes');
  });

  it('colours a probed service by its reachability and says why in words', () => {
    expect([node('s:gateway'), node('s:ordering'), node('s:catalog')].map((n) => `${n.state}: ${n.stateText}`)).toEqual(
      ['up: reachable', 'down: answers 503', 'down: refuses connections'],
    );
  });

  it('colours a worker by its Compose state and health, and never green without a state to read', () => {
    const states = (service: object | null) =>
      node('s:shipping', undefined, {
        ...stack,
        backend: { ...stack.backend, services: service ? [service] : [] },
      } as StackView);
    const compose = { service: 'shipping-worker', state: 'running', health: null, exitCode: null, publishedPorts: [] };

    expect(states(compose).state).toBe('up');
    expect(states({ ...compose, health: 'starting' }).state).toBe('starting');
    expect(states({ ...compose, health: 'unhealthy' })).toMatchObject({
      state: 'down',
      stateText: 'Compose: unhealthy',
    });
    expect(states({ ...compose, state: 'exited' })).toMatchObject({ state: 'down', stateText: 'Compose: exited' });
    expect(states(null)).toMatchObject({ state: 'unknown', stateText: 'no Compose service' });
    expect(
      node('s:shipping', undefined, { ...stack, backend: { ...stack.backend, reachable: false } } as StackView).state,
    ).toBe('unknown');
    expect(node('s:shipping', undefined, null)).toMatchObject({ state: 'unknown', stateText: 'not read yet' });
  });

  it('draws a service the probe does not cover as unknown, not green', () => {
    const unprobed = { ...stack, reachability: [] } as unknown as StackView;

    expect(node('s:gateway', undefined, unprobed)).toMatchObject({ state: 'unknown', stateText: 'not probed' });
  });

  it('colours a queue parked over growing over holding over empty, and unknown when the broker does not list it', () => {
    expect(node('q:ordering-catalog-events', { 'ordering-catalog-events': 1 })).toMatchObject({
      state: 'parked',
      stateText: 'parked: error 1',
      parkedQueue: 'ordering-catalog-events_error',
      label: 'ordering-catalog-events\nparked 1',
    });
    const quiet = {
      ...map,
      services: map.services.map((s) => ({ ...s, queues: s.queues.filter((q) => q.parked === null) })),
    };
    const state = (m: PlatformMapView, rates: Record<string, number> = {}) =>
      topologyOf(m, reads(rates)).nodes.find((n) => n.id === 'q:ordering-catalog-events')?.state;
    expect(state(quiet, { 'ordering-catalog-events': 0.2 })).toBe('growing');
    expect(state(quiet, { 'ordering-catalog-events': -1 })).toBe('holding');
    expect(state(quiet)).toBe('holding');
    expect(node('q:shipping-orders').state).toBe('empty');
    const unlisted = {
      ...quiet,
      services: quiet.services.map((s) => ({ ...s, queues: s.queues.map((q) => ({ ...q, messages: null })) })),
    };
    expect(state(unlisted)).toBe('unknown');
  });

  it('keeps a parked queue with no queue of its own as a node, so it is never left out', () => {
    const orphan = {
      ...map,
      services: map.services.map((s) =>
        s.name === 'shipping'
          ? {
              ...s,
              queues: [{ name: 'lost_skipped', consumer: 'Shipping', messages: 3, parked: 'skipped', bindings: [] }],
            }
          : s,
      ),
    };

    expect(topologyOf(orphan, reads()).nodes.find((n) => n.id === 'q:lost_skipped')).toMatchObject({
      state: 'parked',
      parkedQueue: 'lost_skipped',
    });
  });

  it('opens the error twin before the skipped one when both hold messages', () => {
    const both = {
      ...map,
      services: map.services.map((s) =>
        s.name === 'ordering'
          ? {
              ...s,
              queues: [
                ...s.queues,
                {
                  name: 'ordering-catalog-events_skipped',
                  consumer: 'x',
                  messages: 4,
                  parked: 'skipped',
                  bindings: [],
                },
              ],
            }
          : s,
      ),
    };

    const queue = topologyOf(both, reads()).nodes.find((n) => n.id === 'q:ordering-catalog-events');
    expect(queue?.stateText).toBe('parked: error 1, skipped 4');
    expect(queue?.parkedQueue).toBe('ordering-catalog-events_error');
  });
});

describe('composeOf', () => {
  it('finds the Compose service of a platform service, and none for a name the table does not hold', () => {
    expect(['gateway', 'bff', 'shipping', 'notifications'].map((name) => composeOf({ name }))).toEqual([
      'gateway',
      'web-bff',
      'shipping-worker',
      'notifications-worker',
    ]);
    expect(composeOf({ name: 'nothing-like-it' })).toBeNull();
  });

  it('names only Compose services the Logs widget knows', () => {
    expect(Object.values(COMPOSE_OF).filter((c) => !COMPOSE_SERVICES.includes(c))).toEqual([]);
  });
});

describe('positionsOf', () => {
  it('keeps a node id with two finite numbers, rounded, and drops the rest', () => {
    expect(
      positionsOf({
        'q:a': { x: 10.4, y: -3.6 },
        'q:b': { x: 'left', y: 1 },
        'q:c': { x: Number.POSITIVE_INFINITY, y: 1 },
        'q:d': null,
        'q:e': { x: 1 },
      }),
    ).toEqual({ 'q:a': { x: 10, y: -4 } });
  });

  it('is empty for anything that is not an object, and bounded', () => {
    expect([positionsOf(undefined), positionsOf('x'), positionsOf([1]), positionsOf(null)]).toEqual([{}, {}, {}, {}]);
    const many = Object.fromEntries(Array.from({ length: 500 }, (_, i) => [`n${i}`, { x: i, y: i }]));
    expect(Object.keys(positionsOf(many))).toHaveLength(200);
  });
});
