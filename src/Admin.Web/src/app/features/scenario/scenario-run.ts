import { ApiCatalogView, ApiOperation } from '../../core/host/host-types';
import { PUBLISH_OPERATION } from '../api/api-page';

/**
 * Every step a script can take: run-locally.md's calls, in the order that document gives them, and
 * the watches that read the order back through the BFF's order read until it shows a step.
 */
export type StepKey =
  | 'publish'
  | 'drain'
  | 'quote'
  | 'order'
  | 'cancel'
  | 'confirmed'
  | 'dispatched'
  | 'delivered'
  | 'cancelled';

/** The steps that wait on the order read rather than send once. */
export type WatchKey = Extract<StepKey, 'confirmed' | 'dispatched' | 'delivered' | 'cancelled'>;

export function isWatch(key: StepKey): key is WatchKey {
  return key === 'confirmed' || key === 'dispatched' || key === 'delivered' || key === 'cancelled';
}

export type ScriptKey = 'deliver' | 'cancel';

export interface Script {
  title: string;
  /** One sentence for the page, saying what the run does as run-locally.md does it by hand. */
  intro: string;
  steps: readonly StepKey[];
}

export const SCRIPT_KEYS: readonly ScriptKey[] = ['deliver', 'cancel'];

export const SCRIPTS: Record<ScriptKey, Script> = {
  deliver: {
    title: 'Order to delivered',
    intro:
      'Publish a product, wait for ordering-catalog-events to drain, quote a basket holding it, order it, and read the order back until the platform has confirmed, despatched and delivered it.',
    steps: ['publish', 'drain', 'quote', 'order', 'confirmed', 'dispatched', 'delivered'],
  },
  cancel: {
    title: 'Order and cancel',
    intro:
      'Publish a product, wait for ordering-catalog-events to drain, quote a basket holding it, order it, cancel the order, and read it back until the platform says it is cancelled.',
    steps: ['publish', 'drain', 'quote', 'order', 'cancel', 'cancelled'],
  },
};

export const STEP_TITLES: Record<StepKey, string> = {
  publish: 'Publish a product',
  drain: 'Wait for the projection',
  quote: 'Quote a basket',
  order: 'Place an order',
  cancel: 'Cancel it',
  confirmed: 'Wait for confirmed',
  dispatched: 'Wait for despatch',
  delivered: 'Wait for delivery',
  cancelled: 'Wait for the cancellation',
};

/**
 * What each watch is waiting for, said while it waits, because on an asynchronous platform "nothing
 * is happening" is the normal state for a while. Shipping books the carrier and reads its tracking on
 * timers of its own (blueprint-backend `CarrierHop.FulfilmentTick`, `CarrierHop.TrackingPollInterval`),
 * which is why the two Shipping steps wait in silence before they move.
 */
export const WATCHING_FOR: Record<WatchKey, string> = {
  confirmed: 'the fulfilment saga to reserve the stock, authorise the payment and confirm the order',
  dispatched: 'Shipping to book the carrier for the confirmed order, which it does on a timer of its own',
  delivered: "Shipping's tracking to read the carrier's delivered event, which it polls on a timer",
  cancelled: 'the saga to act on the cancel',
};

/** The calls the steps make, each by its role. */
export type OperationRole = 'publish' | 'quote' | 'order' | 'cancel' | 'read';

/**
 * The catalog operation each role sends, by the id the host gives it: `<source>:<operationId>` for
 * the OpenAPI ones (the backend's `WithName` in ProductEndpoints.cs and OrderEndpoints.cs) and the
 * curated BFF quote and order read (Admin.Host `CuratedOperations`). A body is that operation's
 * example, which `RunLocallyExamples` owns.
 */
export const SCENARIO_OPERATIONS: Record<OperationRole, string> = {
  publish: `catalog:${PUBLISH_OPERATION}`,
  quote: 'bff:Quote',
  order: 'ordering:PlaceOrder',
  cancel: 'ordering:CancelOrder',
  read: 'bff:GetOrder',
};

/** The role a step sends as; the drain asks the broker and sends none. */
export function roleOf(key: StepKey): OperationRole | null {
  if (key === 'drain') return null;
  return isWatch(key) ? 'read' : key;
}

export type ScenarioOperations = Partial<Record<OperationRole, ApiOperation>>;

/**
 * Every operation the script needs, or the reasons it cannot start: one missing from the catalog,
 * or listed as unavailable because its service's document did not load.
 */
export function resolveOperations(
  view: ApiCatalogView,
  script: Script,
): { operations: ScenarioOperations | null; missing: string[] } {
  const roles = [...new Set(script.steps.map(roleOf).filter((r): r is OperationRole => r !== null))];
  const found: ScenarioOperations = {};
  const missing: string[] = [];
  for (const role of roles) {
    const id = SCENARIO_OPERATIONS[role];
    const operation = view.operations.find((o) => o.id === id);
    if (!operation) {
      missing.push(`${id} is not in the operation catalog`);
    } else if (!operation.available) {
      const error = view.sources.find((s) => s.name === operation.source)?.error;
      const reason = `${id} is unavailable: its service's document did not load`;
      missing.push(error ? `${reason}: ${error}` : reason);
    } else {
      found[role] = operation;
    }
  }
  return { operations: missing.length === 0 ? found : null, missing };
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * The id a publish or a place-order answered with. Both return `Result<Guid>` through
 * `ToHttpResult`, which the platform writes as a bare JSON string holding a Guid; anything else is
 * not read as one, so a malformed answer stops at the step that produced it.
 */
export function idFrom(body: string): string | null {
  try {
    const parsed: unknown = JSON.parse(body);
    return typeof parsed === 'string' && GUID.test(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/**
 * The example body with every `productId` under its `lines` or `items` set to the product just
 * published: run-locally.md's zero Guid stands for "a product id you have". A body that is not a
 * JSON object is returned as it was.
 */
export function withProductId(body: string, productId: string): string {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return body;
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return body;
  const object = parsed as Record<string, unknown>;
  const result: Record<string, unknown> = { ...object };
  for (const key of ['lines', 'items']) {
    const list = object[key];
    if (Array.isArray(list)) {
      result[key] = list.map((line: unknown) =>
        line && typeof line === 'object' && !Array.isArray(line) && 'productId' in line
          ? { ...line, productId }
          : line,
      );
    }
  }
  return JSON.stringify(result, null, 2);
}

/**
 * One run's correlation id for one step. Letters, digits and `-` only, well inside the 128
 * characters the backend adopts (`Common.Web.CorrelationIdExtensions`), so the id the Trace screen
 * is asked for is the one the platform logged.
 */
export function stepCorrelationId(run: string, key: StepKey): string {
  return `scenario-${run}-${key}`;
}

/**
 * The cancellation outcomes an order can end in, as the BFF's order read spells them: blueprint-backend
 * `Web.Bff.Persistence.CancelOutcomes`, which `BuyerStatuses` takes its last three members from.
 */
export const CANCELLATIONS: readonly string[] = ['cancelled', 'out_of_stock', 'declined'];

/** The steps the order read's `timeline` keys, as blueprint-backend `Web.Bff.Orders.OrderTimeline` names them. */
export type TimelineStep = 'placed' | 'confirmed' | 'dispatched' | 'delivered' | 'cancelled';

/** What a watch reads from the BFF's `OrderDetail`; the rest of the body is shown, not read. */
export interface OrderRead {
  status: string;
  timeline: Record<TimelineStep, string | null>;
  asOf: string;
}

const TIMELINE_STEPS: readonly TimelineStep[] = [
  'placed',
  'confirmed',
  'dispatched',
  'delivered',
  'cancelled',
];

/** The order read's answer, or null for a body that is not one, so a malformed answer fails its step. */
export function orderRead(body: string): OrderRead | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return null;
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
  const o = parsed as Record<string, unknown>;
  const t = o['timeline'];
  if (typeof o['status'] !== 'string' || typeof o['asOf'] !== 'string') return null;
  if (!t || typeof t !== 'object' || Array.isArray(t)) return null;
  const timeline = {} as Record<TimelineStep, string | null>;
  for (const step of TIMELINE_STEPS) {
    const at = (t as Record<string, unknown>)[step];
    if (at !== null && typeof at !== 'string') return null;
    timeline[step] = at ?? null;
  }
  return { status: o['status'], timeline, asOf: o['asOf'] };
}

export type WatchVerdict =
  | { kind: 'reached'; at: string }
  | { kind: 'waiting' }
  | { kind: 'failed'; reason: string };

/**
 * Whether one read shows the step a watch waits for. A forward step is read from the timeline, so a
 * read that has already moved past it still counts; an order that ended in a cancellation first will
 * never show it, so the watch stops there rather than waiting out its cap. Waiting for the
 * cancellation, only `cancelled` will do: `out_of_stock` and `declined` are the saga cancelling for
 * a reason of its own, and a delivery means the cancel came too late.
 */
export function verdict(key: WatchKey, read: OrderRead): WatchVerdict {
  if (key === 'cancelled') {
    if (read.status === 'cancelled') {
      return { kind: 'reached', at: read.timeline.cancelled ?? read.asOf };
    }
    if (read.status === 'delivered') {
      return { kind: 'failed', reason: 'The order was delivered: the cancel came too late.' };
    }
    if (CANCELLATIONS.includes(read.status)) {
      return {
        kind: 'failed',
        reason: `The order ended ${read.status}: the saga cancelled it for a reason of its own.`,
      };
    }
    return { kind: 'waiting' };
  }

  const at = read.timeline[key];
  if (at) return { kind: 'reached', at };
  if (CANCELLATIONS.includes(read.status)) {
    return { kind: 'failed', reason: `The order ended ${read.status} before it was ${key}.` };
  }
  return { kind: 'waiting' };
}

/** Seconds from one instant to another, to one decimal place, or null where either is unreadable. */
export function secondsBetween(from: string | null, to: string): number | null {
  if (!from) return null;
  const ms = Date.parse(to) - Date.parse(from);
  return Number.isFinite(ms) ? Math.round(ms / 100) / 10 : null;
}
