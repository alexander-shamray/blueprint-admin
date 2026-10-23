import { MapQueue, MapService, PlatformMapView, StackView } from '../../../core/host/host-types';

/** What a service node is coloured by (widget-workspace spec §8.1); `unknown` is drawn neutral, never green. */
export type ServiceState = 'up' | 'down' | 'starting' | 'unknown';

/** What a queue node is coloured by: parked wins over growing, growing over holding. */
export type QueueState = 'empty' | 'holding' | 'growing' | 'parked' | 'unknown';

export type NodeKind = 'gateway' | 'service' | 'queue' | 'exchange';

export interface TopologyNode {
  id: string;
  kind: NodeKind;
  /** What the operator calls it: a service's or queue's name, an exchange's name. */
  name: string;
  /** What the diagram writes in the node; a queue with parked messages carries a second line saying so. */
  label: string;
  state: ServiceState | QueueState | 'neutral';
  /** The state in words, because colour alone says nothing to some operators. */
  stateText: string;
  /** A service's Compose service, where a Logs widget can be pointed at it. */
  compose: string | null;
  /** A queue's parked twin that holds messages, the one the dead-letter workbench opens; null when none does. */
  parkedQueue: string | null;
  /** A queue's depth and the change in it, in words. */
  flow: string | null;
}

export interface TopologyEdge {
  id: string;
  source: string;
  target: string;
  kind: 'route' | 'consume' | 'binding';
  label: string;
}

export interface Topology {
  nodes: TopologyNode[];
  edges: TopologyEdge[];
}

/** What the diagram reads besides the map: the stack's reachability and Compose states, and a queue's rate of change. */
export interface TopologyReads {
  stack: StackView | undefined;
  /** Messages per second since the read before; undefined until two reads have listed the queue. */
  rate: (queue: string) => number | undefined;
  /** The same rate as the Map widget writes it. */
  rateText: (queue: string) => string;
}

export interface Position {
  x: number;
  y: number;
}

/** The nodes the operator moved, by node id. */
export type Positions = Readonly<Record<string, Position>>;

/** How many positions a config may hold: the platform has a few dozen nodes, and the host bounds a config's size. */
const MAX_POSITIONS = 200;

/** The stored positions that are a node id and two finite numbers; anything else is dropped (spec §6.3). */
export function positionsOf(value: unknown): Positions {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return {};
  const kept: Record<string, Position> = {};
  for (const [id, at] of Object.entries(value)) {
    if (Object.keys(kept).length >= MAX_POSITIONS) break;
    if (typeof at !== 'object' || at === null) continue;
    const { x, y } = at as Record<string, unknown>;
    if (typeof x === 'number' && typeof y === 'number' && Number.isFinite(x) && Number.isFinite(y)) {
      kept[id] = { x: Math.round(x), y: Math.round(y) };
    }
  }
  return kept;
}

/**
 * The Compose service of each platform service, which a Logs widget follows. Copied from `PlatformServices.All` on the
 * host (its `Name` and `ComposeService`); `PlatformServiceDriftTests` reads this literal against it, so a service added,
 * renamed or recomposed fails there. Keep it a plain `name: 'compose-service'` literal, which that test parses.
 */
export const COMPOSE_OF: Readonly<Record<string, string>> = {
  gateway: 'gateway',
  catalog: 'catalog-api',
  ordering: 'ordering-api',
  bff: 'web-bff',
  inventory: 'inventory-api',
  payments: 'payments-api',
  privacy: 'privacy-api',
  shipping: 'shipping-worker',
  notifications: 'notifications-worker',
};

/** The Compose service of a platform service, or null for a name the table does not hold. */
export function composeOf(service: Pick<MapService, 'name'>): string | null {
  return COMPOSE_OF[service.name] ?? null;
}

/** The two words a service node is coloured by: a probed service by reachability, a worker by its Compose state. */
function serviceState(service: MapService, stack: StackView | undefined): { state: ServiceState; text: string } {
  if (!stack) return { state: 'unknown', text: 'not read yet' };
  if (service.url !== null) {
    const probed = stack.reachability.find((r) => r.name === service.name);
    if (!probed) return { state: 'unknown', text: 'not probed' };
    if (probed.up) return { state: 'up', text: 'reachable' };
    return { state: 'down', text: probed.status === null ? 'refuses connections' : `answers ${probed.status}` };
  }
  if (!stack.backend.reachable) return { state: 'unknown', text: 'Compose did not answer' };
  const compose = composeOf(service);
  const status = compose === null ? undefined : stack.backend.services.find((s) => s.service === compose);
  if (!status) return { state: 'unknown', text: 'no Compose service' };
  if (status.state !== 'running') return { state: 'down', text: `Compose: ${status.state}` };
  if (status.health === 'unhealthy') return { state: 'down', text: 'Compose: unhealthy' };
  if (status.health === 'starting') return { state: 'starting', text: 'Compose: starting' };
  return { state: 'up', text: 'Compose: running' };
}

/** The queue a parked twin (`<queue>_error`, `<queue>_skipped`) belongs to. */
function baseOf(twin: MapQueue): string {
  return twin.name.replace(/_(error|skipped)$/, '');
}

/**
 * The platform as a graph (widget-workspace spec §8.1), from the host's map and the stack. Nodes: the gateway, each
 * service, each queue, and the exchanges the queues are bound from. Edges: gateway to the service its routes reach;
 * exchange to queue, carrying the queue's depth and rate; queue to the service that consumes it. A queue's parked twins
 * are folded into its node as a badge. Nothing here is drawn; the same graph is listed beside the drawing.
 */
export function topologyOf(map: PlatformMapView, reads: TopologyReads): Topology {
  const nodes: TopologyNode[] = [];
  const edges: TopologyEdge[] = [];
  const exchanges = new Set<string>();
  const gateway = map.services.find((s) => s.name === 'gateway');

  for (const service of map.services) {
    const { state, text } = serviceState(service, reads.stack);
    nodes.push({
      id: `s:${service.name}`,
      kind: service === gateway ? 'gateway' : 'service',
      name: service.name,
      label: service.name,
      state,
      stateText: text,
      compose: composeOf(service),
      parkedQueue: null,
      flow: null,
    });
    if (gateway && service !== gateway && service.routes.length > 0) {
      edges.push({
        id: `route:${service.name}`,
        source: `s:${gateway.name}`,
        target: `s:${service.name}`,
        kind: 'route',
        label: service.routes.length === 1 ? service.routes[0].prefix : `${service.routes.length} routes`,
      });
    }
  }

  const queues = map.services.flatMap((service) => service.queues.map((queue) => ({ service, queue })));
  const mains = new Set(queues.filter((q) => q.queue.parked === null).map((q) => q.queue.name));
  for (const { service, queue } of queues) {
    // A twin with no queue of its own to fold into stands as a node, so a parked queue is never left out.
    if (queue.parked !== null && mains.has(baseOf(queue))) continue;
    const twins =
      queue.parked === null
        ? queues.filter((q) => q.queue.parked !== null && baseOf(q.queue) === queue.name).map((q) => q.queue)
        : [queue];
    const parkedMessages = twins.reduce((n, t) => n + (t.messages ?? 0), 0);
    const holding = twins
      .filter((t) => (t.messages ?? 0) > 0)
      .sort((a, b) => (a.parked === 'error' ? -1 : b.parked === 'error' ? 1 : 0));
    const rate = reads.rate(queue.name);
    const depth = queue.messages === null ? '—' : String(queue.messages);

    let state: QueueState;
    let text: string;
    if (parkedMessages > 0) {
      state = 'parked';
      text = `parked: ${holding.map((t) => `${t.parked} ${t.messages}`).join(', ')}`;
    } else if (queue.messages === null) {
      state = 'unknown';
      text = 'the broker does not list it';
    } else if (rate !== undefined && rate > 0) {
      state = 'growing';
      text = 'growing';
    } else if (queue.messages > 0) {
      state = 'holding';
      text = 'holding';
    } else {
      state = 'empty';
      text = 'empty';
    }

    const id = `q:${queue.name}`;
    const flow = `${depth} · ${reads.rateText(queue.name)}`;
    nodes.push({
      id,
      kind: 'queue',
      name: queue.name,
      label: parkedMessages > 0 ? `${queue.name}\nparked ${parkedMessages}` : queue.name,
      state,
      stateText: text,
      compose: null,
      parkedQueue: holding[0]?.name ?? null,
      flow,
    });
    // The depth and rate ride the edge into the queue; a queue nothing is bound to carries them on its way out instead.
    const bindings = queue.parked === null ? queue.bindings : [];
    for (const b of bindings) {
      exchanges.add(b.source);
      edges.push({
        id: `bind:${b.source}>${queue.name}:${b.routingKey}`,
        source: `x:${b.source}`,
        target: id,
        kind: 'binding',
        label: flow,
      });
    }
    edges.push({
      id: `consume:${queue.name}`,
      source: id,
      target: `s:${service.name}`,
      kind: 'consume',
      label: bindings.length === 0 ? flow : '',
    });
  }

  for (const name of exchanges) {
    nodes.push({
      id: `x:${name}`,
      kind: 'exchange',
      name,
      label: name,
      state: 'neutral',
      stateText: 'exchange',
      compose: null,
      parkedQueue: null,
      flow: null,
    });
  }
  return { nodes, edges };
}
