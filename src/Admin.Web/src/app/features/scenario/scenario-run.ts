import { ApiCatalogView, ApiOperation } from '../../core/host/host-types';
import { PUBLISH_OPERATION } from '../api/api-page';

/**
 * Every step a script can take: run-locally.md's calls, in the order that document gives them, and
 * the watches that read the order back through the BFF's order read until it shows a step.
 */
export type StepKey =
  | 'upload'
  | 'publish'
  | 'thumbnail'
  | 'drain'
  | 'quote'
  | 'stock'
  | 'order'
  | 'cancel'
  | 'confirmed'
  | 'dispatched'
  | 'delivered'
  | 'cancelled'
  | 'declined'
  | 'out_of_stock';

/** The steps that wait on the order read rather than send once. */
export type WatchKey = Extract<
  StepKey,
  'confirmed' | 'dispatched' | 'delivered' | 'cancelled' | 'declined' | 'out_of_stock'
>;

const WATCHES: readonly StepKey[] = [
  'confirmed',
  'dispatched',
  'delivered',
  'cancelled',
  'declined',
  'out_of_stock',
];

export function isWatch(key: StepKey): key is WatchKey {
  return WATCHES.includes(key);
}

export type ScriptKey = 'deliver' | 'cancel' | 'declined' | 'out-of-stock' | 'image';

export interface Script {
  title: string;
  /** One sentence for the page, saying what the run does as run-locally.md does it by hand. */
  intro: string;
  steps: readonly StepKey[];
  /**
   * The cause of an unhappy branch, each one a paragraph of run-locally.md: the amount the product
   * is published at, and the stock set on hand before the order.
   */
  amount?: number;
  onHand?: number;
}

export const SCRIPT_KEYS: readonly ScriptKey[] = [
  'deliver',
  'cancel',
  'declined',
  'out-of-stock',
  'image',
];

/** run-locally.md, "A declined payment is caused by the amount": a price ending `.01` is `card_declined`. */
const DECLINED_AMOUNT = 10.01;

/** run-locally.md's stock example, "Stock for a product, and setting it": enough for the order to reserve. */
const STOCKED = 10;

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
  declined: {
    title: 'Payment declined',
    intro:
      'Publish a product at 10.01, because the payment simulator declines an amount whose minor units are .01, stock it, order it, and read the order back until the saga says it was declined.',
    steps: ['publish', 'drain', 'stock', 'quote', 'order', 'declined'],
    amount: DECLINED_AMOUNT,
    onHand: STOCKED,
  },
  // A script of its own, not two steps woven into the order scripts: the store is not the saga, and an order script
  // that failed on a broken store would hide the saga regression it exists to find.
  image: {
    title: 'Product with an image',
    intro:
      'Ask the BFF for a pre-signed upload, PUT an image to the object store, publish a product with the key it returned, read the product back, and fetch its thumbnail from image-edge as a buyer\'s browser would.',
    steps: ['upload', 'publish', 'thumbnail'],
  },
  'out-of-stock': {
    title: 'Reservation failed',
    intro:
      'Publish a product, set its stock on hand to 0, order it, and read the order back until the saga says it is out of stock.',
    steps: ['publish', 'drain', 'stock', 'quote', 'order', 'out_of_stock'],
    onHand: 0,
  },
};

export const STEP_TITLES: Record<StepKey, string> = {
  upload: 'Upload a product image',
  publish: 'Publish a product',
  thumbnail: 'Read the image back through the edge',
  drain: 'Wait for the projection',
  quote: 'Quote a basket',
  stock: 'Set the stock on hand',
  order: 'Place an order',
  cancel: 'Cancel it',
  confirmed: 'Wait for confirmed',
  dispatched: 'Wait for despatch',
  delivered: 'Wait for delivery',
  cancelled: 'Wait for the cancellation',
  declined: 'Wait for the decline',
  out_of_stock: 'Wait for the stock refusal',
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
  declined: 'the saga to ask the payment provider to authorise the order and be refused',
  out_of_stock: 'the saga to ask Inventory to reserve the stock and be refused',
};

/** The calls the steps make, each by its role. */
export type OperationRole =
  | 'image'
  | 'publish'
  | 'product'
  | 'stock'
  | 'quote'
  | 'order'
  | 'cancel'
  | 'read';

/**
 * The catalog operation each role sends, by the id the host gives it: `<source>:<operationId>` for
 * the OpenAPI ones (the backend's `WithName` in ProductEndpoints.cs and OrderEndpoints.cs) and the
 * curated BFF quote and order read (Admin.Host `CuratedOperations`). A body is that operation's
 * example, which `RunLocallyExamples` owns.
 */
export const SCENARIO_OPERATIONS: Record<OperationRole, string> = {
  image: 'bff:IssueProductImageUpload',
  publish: `catalog:${PUBLISH_OPERATION}`,
  product: 'catalog:GetProduct',
  stock: 'inventory:SetOnHand',
  quote: 'bff:Quote',
  order: 'ordering:PlaceOrder',
  cancel: 'ordering:CancelOrder',
  read: 'bff:GetOrder',
};

/** The role a step sends as; the drain asks the broker and sends none. */
export function roleOf(key: StepKey): OperationRole | null {
  if (key === 'drain') return null;
  if (key === 'upload') return 'image';
  if (key === 'thumbnail') return 'product';
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
 * A JSON object body with `name` set to `value`; a body that is not a JSON object is returned as it
 * was. The scripts set only what run-locally.md names as the cause: the publish's `amount`, the
 * stock write's `onHand`.
 */
export function withField(body: string, name: string, value: number | string): string {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return body;
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return body;
  return JSON.stringify({ ...(parsed as Record<string, unknown>), [name]: value }, null, 2);
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
  if (key === 'declined' || key === 'out_of_stock') {
    // The saga's own refusals: the status the BFF gives the cause is the only one that will do.
    if (read.status === key) {
      return { kind: 'reached', at: read.timeline.cancelled ?? read.asOf };
    }
    if (read.status === 'delivered' || CANCELLATIONS.includes(read.status)) {
      return { kind: 'failed', reason: `The order ended ${read.status}, not ${key}.` };
    }
    return { kind: 'waiting' };
  }

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

/** What the BFF's `POST /bff/v1/product-images` returns: the pre-signed URL, the headers it signed, and the key to publish with. */
export interface ImageUpload {
  uploadUrl: string;
  headers: Record<string, string>;
  key: string;
}

/**
 * The upload the BFF issued (blueprint-backend `ProductImageUpload`), or null for a body that is not one, so
 * a malformed answer stops the run at the step that produced it instead of PUTting to nowhere.
 */
export function uploadFrom(body: string): ImageUpload | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return null;
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
  const { uploadUrl, headers, key } = parsed as Record<string, unknown>;
  if (typeof uploadUrl !== 'string' || typeof key !== 'string' || !uploadUrl || !key) return null;
  const signed: Record<string, string> = {};
  if (headers && typeof headers === 'object' && !Array.isArray(headers)) {
    for (const [name, value] of Object.entries(headers)) {
      if (typeof value === 'string') signed[name] = value;
    }
  }
  return { uploadUrl, headers: signed, key };
}

/** The `thumbnailUrl` of a product read, or null where the product carries none (it has no image). */
export function thumbnailFrom(body: string): string | null {
  try {
    const parsed: unknown = JSON.parse(body);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
    const url = (parsed as Record<string, unknown>)['thumbnailUrl'];
    return typeof url === 'string' && url ? url : null;
  } catch {
    return null;
  }
}

/** One response header's first value, found without regard to case, as HTTP names ignore it. */
export function headerOf(headers: Record<string, string[]>, name: string): string | null {
  const found = Object.entries(headers).find(([key]) => key.toLowerCase() === name.toLowerCase());
  return found?.[1][0] ?? null;
}

/**
 * What a buyer's browser needs of the stored image: the edge answered, said it is an image, and set `nosniff`, which
 * the store does not and the edge must (blueprint-backend `deploy/compose/object-store/edge.conf`). Null is a pass;
 * otherwise the reason it is not one.
 */
export function imageFault(status: number, headers: Record<string, string[]>): string | null {
  if (status < 200 || status >= 300) return `The edge answered ${status}, not the image.`;
  const type = headerOf(headers, 'Content-Type');
  if (!type?.toLowerCase().startsWith('image/')) {
    return `The edge served ${type ?? 'no content type'}, not an image.`;
  }
  if (headerOf(headers, 'X-Content-Type-Options')?.toLowerCase() !== 'nosniff') {
    return 'The edge did not set X-Content-Type-Options: nosniff.';
  }
  return null;
}
