import { ApiCatalogView, ApiOperation, MailListView, ReviewFlagView } from '../../core/host/host-types';
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
  | 'lease'
  | 'restore'
  | 'cancel'
  | 'late_cancel'
  | 'confirmed'
  | 'dispatched'
  | 'delivered'
  | 'cancelled'
  | 'declined'
  | 'out_of_stock'
  | 'unchanged'
  | 'carrier'
  | 'flagged'
  | 'reserved'
  | 'stop'
  | 'held'
  | 'mail';

/** The steps that wait on the order read rather than send once. */
export type WatchKey = Extract<
  StepKey,
  | 'confirmed'
  | 'dispatched'
  | 'delivered'
  | 'cancelled'
  | 'declined'
  | 'out_of_stock'
  | 'unchanged'
  | 'held'
>;

const WATCHES: readonly StepKey[] = [
  'confirmed',
  'dispatched',
  'delivered',
  'cancelled',
  'declined',
  'out_of_stock',
  'unchanged',
  'held',
];

export function isWatch(key: StepKey): key is WatchKey {
  return WATCHES.includes(key);
}

export type ScriptKey =
  | 'deliver'
  | 'cancel'
  | 'declined'
  | 'out-of-stock'
  | 'in-transit'
  | 'already-shipped'
  | 'stock-timeout'
  | 'payment-timeout'
  | 'despatch-timeout'
  | 'release-timeout'
  | 'carrier-refused'
  | 'carrier-down'
  | 'carrier-slow'
  | 'carrier-strange'
  | 'image';

/** What the carrier simulator's request log shows of each code the order read cannot tell apart (run-locally.md, the carrier log). */
export type CarrierOutcome = 'refused' | 'down' | 'slow' | 'strange';

/** The review Ordering raises when a wait of the saga lapses (blueprint-backend `OrderReviews`), by the reason it records. */
export interface ScriptReview {
  reason: string;
}

/** The code an order is placed to and what the simulator's log must then show of it. */
export interface ScriptCarrier {
  code: string;
  outcome: CarrierOutcome;
}

/**
 * A change to the stack the script holds for its cause (spec §5.3, Scenario service control): the ids of
 * the host's `ScenarioControl.Table`, and how long the host holds them before it restores them itself. The `lease` step takes
 * it and the `restore` step ends it, which runs whatever happened between.
 */
export interface ScriptLease {
  changes: readonly string[];
  holdSeconds: number;
}

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
  /**
   * The delivery postal code the order is placed to, which scripts the carrier simulator (run-locally.md, "The carrier's
   * answer is scripted by the delivery postal code"). Unset, the example body's own code takes the default outcome.
   */
  postalCode?: string;
  /** The change to the stack its cause needs, taken by the `lease` step and always ended by the `restore` step. */
  lease?: ScriptLease;
  /** The carrier outcome its `carrier` step reads from the simulator's request log. */
  carrier?: ScriptCarrier;
  /** The reason Ordering must log when it flags the order for review, which its `flagged` step reads (run-locally.md, the Loki query). */
  review?: ScriptReview;
  /**
   * A `ScenarioControl.Table` id its `stop` step takes while the lease is held, after the steps before it: the cause needs the
   * stack as it was until then (the stock reserved first, then Inventory stopped), which a change taken with the lease cannot give.
   */
  lateChange?: string;
  /**
   * The template keys of the mail the run must have sent by its end, one message each (`MAIL_EVENTS` names the event each
   * answers), which its `mail` step counts in the mail sink.
   */
  mail?: readonly string[];
}

export const SCRIPT_KEYS: readonly ScriptKey[] = [
  'deliver',
  'cancel',
  'declined',
  'out-of-stock',
  'in-transit',
  'already-shipped',
  'stock-timeout',
  'payment-timeout',
  'despatch-timeout',
  'release-timeout',
  'carrier-refused',
  'carrier-down',
  'carrier-slow',
  'carrier-strange',
  'image',
];

/** How the carrier step polls the simulator's log and how long it waits: Shipping backs off after a refusal, so a minute or two (run-locally.md). */
export const CARRIER_POLL_MS = 5000;
export const CARRIER_WATCH_MS = 240_000;

/**
 * How the flagged step reads Ordering's log line and how long it waits: the despatch wait is 60 s with the short-timeouts
 * override (`deploy/compose/docker-compose.short-timeouts.yml`) and Loki lags the log a few seconds behind it.
 */
export const REVIEW_POLL_MS = 5000;
export const REVIEW_WATCH_MS = 150_000;

/**
 * How the reserved step reads Inventory's reservation: the saga asks for it as soon as the order is placed, so a few seconds,
 * and a minute covers the projection and a slow start.
 */
export const RESERVATION_POLL_MS = 1000;
export const RESERVATION_WATCH_MS = 60_000;

/** run-locally.md's carrier table: the postal code each outcome is scripted by. */
const CARRIER_STEPS: readonly StepKey[] = ['publish', 'drain', 'stock', 'quote', 'order', 'confirmed', 'carrier', 'held', 'mail'];

/** The host's `ScenarioControl` change ids (ids, not copies of what they do: the host's table owns that). */
const SHORT_WAITS = 'short-waits';
const STOP_INVENTORY = 'stop-inventory';

/**
 * run-locally.md, "The branches, then": a total ending .09 is the payment simulator answering after the adapter's own
 * budget, which holds Payments up, so the order is declined only by the saga's payment wait.
 */
const PAYMENT_WAIT_AMOUNT = 10.09;

/** run-locally.md's carrier table: `SIM-STRANGE` books the parcel and never has it collected, so the despatch wait lapses. */
const STRANGE_CODE = 'SIM-STRANGE';

/** run-locally.md's carrier table: `SIM-TRANSIT` is collected and never delivered, so the order read stays `dispatched`. */
const IN_TRANSIT_CODE = 'SIM-TRANSIT';

/** run-locally.md, "A declined payment is caused by the amount": a price ending `.01` is `card_declined`. */
const DECLINED_AMOUNT = 10.01;

/** run-locally.md's stock example, "Stock for a product, and setting it": enough for the order to reserve. */
const STOCKED = 10;

export const SCRIPTS: Record<ScriptKey, Script> = {
  deliver: {
    title: 'Order to delivered',
    intro:
      'Publish a product, wait for ordering-catalog-events to drain, set its stock on hand, quote a basket holding it, order it, and read the order back until the platform has confirmed, despatched and delivered it.',
    steps: ['publish', 'drain', 'stock', 'quote', 'order', 'confirmed', 'dispatched', 'delivered', 'mail'],
    onHand: STOCKED,
    mail: ['order-placed', 'order-confirmed', 'shipment-dispatched', 'shipment-delivered'],
  },
  cancel: {
    title: 'Order and cancel',
    intro:
      'Publish a product, wait for ordering-catalog-events to drain, set its stock on hand, quote a basket holding it, order it, cancel the order, and read it back until the platform says it is cancelled.',
    steps: ['publish', 'drain', 'stock', 'quote', 'order', 'cancel', 'cancelled', 'mail'],
    onHand: STOCKED,
    // The cancel is asked a fraction of a second after the place, long before the saga confirms: no confirmation is mailed.
    mail: ['order-placed', 'order-cancelled'],
  },
  declined: {
    title: 'Payment declined',
    intro:
      'Publish a product at 10.01, because the payment simulator declines an amount whose minor units are .01, stock it, order it, and read the order back until the saga says it was declined.',
    steps: ['publish', 'drain', 'stock', 'quote', 'order', 'declined', 'mail'],
    amount: DECLINED_AMOUNT,
    onHand: STOCKED,
    // The saga compensates a declined payment by cancelling the order, so Notifications mails both events.
    mail: ['order-placed', 'order-cancelled', 'payment-declined'],
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
    steps: ['publish', 'drain', 'stock', 'quote', 'order', 'out_of_stock', 'mail'],
    onHand: 0,
    mail: ['order-placed', 'order-cancelled'],
  },
  'in-transit': {
    title: 'Parcel collected, never delivered',
    intro:
      'Publish a product, stock it, order it to the delivery postal code SIM-TRANSIT, which scripts the carrier simulator to collect the parcel and never deliver it, and read the order back until it is despatched. It stays despatched.',
    steps: ['publish', 'drain', 'stock', 'quote', 'order', 'confirmed', 'dispatched'],
    onHand: STOCKED,
    postalCode: IN_TRANSIT_CODE,
  },
  'already-shipped': {
    title: 'Cancel after despatch',
    intro:
      'Order a stocked product to SIM-TRANSIT, wait until the order is despatched, ask to cancel it, expect the platform to refuse with 422 order.already_shipped, and read the order back unchanged.',
    steps: ['publish', 'drain', 'stock', 'quote', 'order', 'confirmed', 'dispatched', 'late_cancel', 'unchanged'],
    onHand: STOCKED,
    postalCode: IN_TRANSIT_CODE,
  },
  'stock-timeout': {
    title: 'Stock timeout',
    intro:
      "Shorten the saga's waits and stop Inventory, so nothing can answer the reservation; publish a product, order it, and read the order back until the saga's own stock wait has lapsed and ended it out of stock. Only that wait can have ended it. The stack is restored afterwards, whatever happens.",
    steps: ['lease', 'publish', 'drain', 'order', 'out_of_stock', 'mail', 'restore'],
    lease: { changes: [SHORT_WAITS, STOP_INVENTORY], holdSeconds: 420 },
    // Notifications is not Inventory: the cancel is mailed with Inventory stopped (read live, 2 of 2 runs).
    mail: ['order-placed', 'order-cancelled'],
  },
  'payment-timeout': {
    title: 'Payment timeout',
    intro:
      "Shorten the saga's waits, publish a product at 10.09 (the payment simulator answers a total ending .09 after the adapter's own budget), stock it, order it, and read the order back until the saga's payment wait has lapsed and declined it. A .09 order holds Payments up while it waits, so run nothing else beside it. The stack is restored afterwards, whatever happens.",
    steps: ['lease', 'publish', 'drain', 'stock', 'quote', 'order', 'declined', 'mail', 'restore'],
    amount: PAYMENT_WAIT_AMOUNT,
    onHand: STOCKED,
    lease: { changes: [SHORT_WAITS], holdSeconds: 360 },
    // The saga compensates the lapsed payment wait as it does a declined payment (read live, 2 of 2 runs).
    mail: ['order-placed', 'order-cancelled', 'payment-declined'],
  },
  'despatch-timeout': {
    title: 'Despatch timeout',
    intro:
      "Shorten the saga's waits, publish a product, stock it, and order it to the delivery postal code SIM-STRANGE, whose parcel is booked and never collected; read Ordering's log until the despatch wait has lapsed and it has flagged the order for review as not_despatched, and read the order back still confirmed. Nothing compensates it: a person owns it now. The stack is restored afterwards, whatever happens.",
    steps: ['lease', 'publish', 'drain', 'stock', 'quote', 'order', 'confirmed', 'flagged', 'held', 'mail', 'restore'],
    onHand: STOCKED,
    postalCode: STRANGE_CODE,
    lease: { changes: [SHORT_WAITS], holdSeconds: 420 },
    review: { reason: 'not_despatched' },
    // A review flag mails nothing: the order stays confirmed (read live, 2 of 2 runs).
    mail: ['order-placed', 'order-confirmed'],
  },
  'release-timeout': {
    title: 'Release timeout',
    intro:
      "Shorten the saga's waits, publish a product at 10.09 (a total ending .09 holds the payment, so the stock is reserved and the order is not yet confirmed), stock it, order it, wait until Inventory has reserved the stock, stop Inventory, and cancel the order; read the order cancelled while the release the saga asks for is never answered, and Ordering's log until it has flagged the order for review as stock_not_released. A .09 order holds Payments up while it waits, so run nothing else beside it. The stack is restored afterwards, whatever happens.",
    steps: [
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
    ],
    amount: PAYMENT_WAIT_AMOUNT,
    onHand: STOCKED,
    lease: { changes: [SHORT_WAITS], holdSeconds: 420 },
    lateChange: STOP_INVENTORY,
    review: { reason: 'stock_not_released' },
    // Cancelled before the held payment answered, so no confirmation (read live, 2 of 2 runs).
    mail: ['order-placed', 'order-cancelled'],
  },
  'carrier-refused': carrierScript(
    'Carrier refuses the address',
    'SIM-REFUSED',
    'refused',
    'the carrier refuses the booking with 422 address_not_serviceable',
  ),
  'carrier-down': carrierScript(
    'Carrier is down',
    'SIM-DOWN',
    'down',
    'the carrier answers every booking attempt 503, and Shipping tries again',
  ),
  'carrier-slow': carrierScript(
    'Carrier is slow',
    'SIM-SLOW',
    'slow',
    'the carrier answers after Shipping has stopped waiting, so the same booking is tried again',
  ),
  'carrier-strange': carrierScript(
    'Carrier feed is strange',
    STRANGE_CODE,
    'strange',
    "the carrier books the parcel and then serves a feed Shipping refuses whole, so it is polled again and again",
  ),
};

/**
 * One of the four carrier outcomes the order read cannot tell apart (run-locally.md's carrier table): the order is placed to
 * the code that scripts the simulator, and the simulator's own request log says what the carrier saw. The order read is then
 * held to what the document says it shows, a confirmed order. Only the carrier's side is asserted: the shipment's own state
 * (`Unfulfillable`, `Pending`, `Booked`) is in a table the console does not query.
 */
function carrierScript(title: string, code: string, outcome: CarrierOutcome, saw: string): Script {
  return {
    title,
    intro: `Publish a product, stock it, order it to the delivery postal code ${code}, and read the carrier simulator's request log until it shows ${saw}. The order read is then held to what run-locally.md says it shows: confirmed, never despatched. Only what the carrier saw is asserted, not the shipment's own state.`,
    steps: CARRIER_STEPS,
    onHand: STOCKED,
    postalCode: code,
    carrier: { code, outcome },
    // Confirmed and never despatched, whatever the carrier did: read live, 10 of 10 runs across the four outcomes.
    mail: ['order-placed', 'order-confirmed'],
  };
}

/** What run-locally.md ("After despatch there is nothing to compensate") observed a cancel after despatch answer. */
export const ALREADY_SHIPPED = { status: 422, code: 'order.already_shipped' } as const;

export const STEP_TITLES: Record<StepKey, string> = {
  upload: 'Upload a product image',
  publish: 'Publish a product',
  thumbnail: 'Read the image back through the edge',
  drain: 'Wait for the projection',
  quote: 'Quote a basket',
  stock: 'Set the stock on hand',
  order: 'Place an order',
  cancel: 'Cancel it',
  lease: 'Change the stack for the cause',
  restore: 'Restore the stack',
  late_cancel: 'Ask to cancel it after despatch',
  confirmed: 'Wait for confirmed',
  dispatched: 'Wait for despatch',
  delivered: 'Wait for delivery',
  cancelled: 'Wait for the cancellation',
  declined: 'Wait for the decline',
  out_of_stock: 'Wait for the stock refusal',
  unchanged: 'Read the order back unchanged',
  carrier: 'Read what the carrier saw',
  flagged: 'Read the review the platform raised',
  reserved: 'Wait for the stock to be reserved',
  stop: 'Stop Inventory now the stock is reserved',
  held: 'Read the order back still confirmed',
  mail: 'Count the mail the order sent',
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
  unchanged: 'the order read, which a refused cancel leaves where it was: despatched and no longer cancellable',
  held: 'the order read, which a carrier that refused, was down or slow, or served a strange feed, and a despatch that never comes, leave at confirmed',
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
  | 'read'
  | 'bookings'
  | 'polls'
  | 'reservation'
  | 'saga';

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
  bookings: 'carrier:FindBookings',
  polls: 'carrier:FindFeedPolls',
  reservation: 'inventory:GetReservation',
  // Beside the order read, not a step of its own: a catalog without it leaves the strip with the BFF's column alone.
  saga: 'ordering:GetOrderSagaState',
};

/** The role a step sends as; the drain asks the broker and sends none. */
export function roleOf(key: StepKey): OperationRole | null {
  if (key === 'drain' || key === 'lease' || key === 'restore' || key === 'flagged' || key === 'stop' || key === 'mail') return null;
  if (key === 'upload') return 'image';
  if (key === 'thumbnail') return 'product';
  if (key === 'late_cancel') return 'cancel';
  if (key === 'carrier') return 'bookings';
  if (key === 'reserved') return 'reservation';
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
  // A strange feed is read by its polls as well as its bookings; no other step names the second filter.
  if (script.carrier?.outcome === 'strange') roles.push('polls');
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
  // The saga's read is the second column of the strip, never a reason a script cannot run: an older backend lacks it.
  const saga = roles.includes('read') ? view.operations.find((o) => o.id === SCENARIO_OPERATIONS.saga) : undefined;
  if (saga?.available) found.saga = saga;
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

/** Whether the body is a JSON object that carries `name` equal to `value`: what `withField` sets, or declined to. */
export function hasField(body: string, name: string, value: number | string): boolean {
  try {
    const parsed: unknown = JSON.parse(body);
    return (
      !!parsed &&
      typeof parsed === 'object' &&
      !Array.isArray(parsed) &&
      (parsed as Record<string, unknown>)[name] === value
    );
  } catch {
    return false;
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
 * The example order body with the delivery postal code set to `code`: the one field of an order the carrier sees
 * (run-locally.md, "The carrier's answer is scripted by the delivery postal code"). A body that is not a JSON object,
 * or has no `shippingAddress` object, is returned as it was, so the run does not invent an address.
 */
export function withShippingPostalCode(body: string, code: string): string {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return body;
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return body;
  const object = parsed as Record<string, unknown>;
  const address = object['shippingAddress'];
  if (!address || typeof address !== 'object' || Array.isArray(address)) return body;
  return JSON.stringify({ ...object, shippingAddress: { ...address, postalCode: code } }, null, 2);
}

/** Whether the body's `shippingAddress.postalCode` is `code`: what `withShippingPostalCode` sets, or declined to. */
export function hasShippingPostalCode(body: string, code: string): boolean {
  try {
    const parsed: unknown = JSON.parse(body);
    const address = (parsed as Record<string, unknown> | null)?.['shippingAddress'];
    return (
      !!address &&
      typeof address === 'object' &&
      !Array.isArray(address) &&
      (address as Record<string, unknown>)['postalCode'] === code
    );
  } catch {
    return false;
  }
}

/**
 * The simulator's booking filter with its pattern set to `code`: run-locally.md's request names `SIM-DOWN`, and each script
 * asks about the code it placed its order to. A body that is not the filter (no matcher to set) is returned as it was, so the
 * step reads what the document's own filter gives rather than a body this guessed at.
 */
export function withCarrierCode(body: string, code: string): string {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return body;
  }
  const filter = parsed as { Body?: { Matchers?: { Pattern?: unknown }[] } } | null;
  const matcher = filter?.Body?.Matchers?.[0];
  if (!matcher || typeof matcher.Pattern !== 'string') return body;
  matcher.Pattern = `*${code}*`;
  return JSON.stringify(filter, null, 2);
}

/** One request the carrier simulator logged, read from a WireMock.Net entry. */
export interface CarrierEntry {
  /** When the simulator received it, in ms since the epoch (`Request.DateTime`, UTC). */
  at: number;
  method: string;
  path: string;
  status: number;
  /** The booking's own id, the same on every retry of one booking (`Request.Body.shipmentId`), where the request has a body. */
  shipmentId: string | null;
}

/**
 * The entries of the simulator's `find` answer, or null for a body that is not an array of them (an error the simulator
 * sent, or a log cut short by the proxy's cap), so a malformed answer fails the step instead of reading as an empty log.
 */
export function carrierEntries(body: string): CarrierEntry[] | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return null;
  }
  if (!Array.isArray(parsed)) return null;
  const entries: CarrierEntry[] = [];
  for (const raw of parsed as unknown[]) {
    const entry = raw as { Request?: Record<string, unknown>; Response?: Record<string, unknown> } | null;
    const at = Date.parse(String(entry?.Request?.['DateTime']));
    const status = entry?.Response?.['StatusCode'];
    if (!entry?.Request || Number.isNaN(at) || typeof status !== 'number') return null;
    let shipmentId: string | null = null;
    const requestBody = entry.Request['Body'];
    if (typeof requestBody === 'string') {
      try {
        const id = (JSON.parse(requestBody) as { shipmentId?: unknown } | null)?.shipmentId;
        shipmentId = typeof id === 'string' ? id : null;
      } catch {
        shipmentId = null;
      }
    }
    entries.push({
      at,
      method: String(entry.Request['Method'] ?? ''),
      path: String(entry.Request['Path'] ?? ''),
      status,
      shipmentId,
    });
  }
  return entries;
}

/**
 * Every message Notifications sends and the event it answers, as `<template key>=<event>`: blueprint-backend
 * `Notifications.Application.Rendering.TemplateKeys` for the keys and the `Intake/<event>Handler` that records each. The key is
 * what a sent message's `Message-ID` carries after the event's id (`MailMessageId`).
 */
const MAIL_EVENTS = [
  'order-placed=OrderPlaced',
  'order-confirmed=OrderConfirmed',
  'order-cancelled=OrderCancelled',
  'payment-declined=PaymentDeclined',
  'payment-refunded=PaymentRefunded',
  'shipment-dispatched=ShipmentDispatched',
  'shipment-delivered=ShipmentDelivered',
];

/** The event a template's message answers, or the key itself for one this console does not know. */
export function mailEvent(template: string): string {
  const found = MAIL_EVENTS.find((e) => e.startsWith(`${template}=`));
  return found ? found.slice(template.length + 1) : template;
}

/** The mail a run sent: one entry for each event the sink holds a message for, with how many messages answered it. */
export interface MailTally {
  event: string;
  template: string;
  /** The event's id as the `Message-ID` spells it. */
  eventId: string;
  messages: number;
}

/**
 * The order's messages in the sink, counted by the event each answers. A message is the order's when its `X-Correlation-Id`
 * is the order's id: Notifications stamps the header from the integration event's `CorrelationId`, a `Guid` the order's own
 * id fills, so the `scenario-<run>-<step>` id the console sends never reaches the mail (read live, 2026-10-17). Its event is read from the
 * `Message-ID`, `<event id>.<template key>@<domain>`, which Notifications derives from the event's id so that a redelivery
 * that sends twice sends the same id twice. One that does not read that way is counted under its whole id. The tallies come
 * oldest first.
 */
export function tallyMail(view: MailListView, orderId: string): MailTally[] {
  const wanted = orderId.toLowerCase();
  const tallies = new Map<string, MailTally>();
  // The sink lists newest first; the count is told oldest first, the order the events happened in.
  for (const m of [...view.messages].reverse()) {
    if (m.correlationId?.toLowerCase() !== wanted) continue;
    const local = m.messageId.split('@')[0];
    const dot = local.indexOf('.');
    const eventId = dot < 0 ? local : local.slice(0, dot);
    const template = dot < 0 ? '' : local.slice(dot + 1);
    const key = `${eventId}.${template}`;
    const seen = tallies.get(key);
    if (seen) seen.messages++;
    else tallies.set(key, { event: mailEvent(template), template, eventId, messages: 1 });
  }
  return [...tallies.values()];
}

export type MailVerdict =
  | { kind: 'reached'; detail: string }
  | { kind: 'waiting'; detail: string }
  | { kind: 'failed'; reason: string };

/**
 * Whether the sink holds exactly one message for each event the script expects. A second message for an event is the
 * defect the step exists to show and does not wait on a further one: it fails at once, naming the event and the count. A
 * message for an event the script does not expect fails the step too. Missing ones are waited out, since the send worker is
 * asynchronous. A sink that cannot be read fails it: it never reads as a run that sent nothing.
 */
export function mailVerdict(view: MailListView, orderId: string, expected: readonly string[]): MailVerdict {
  if (!view.reachable) {
    return { kind: 'failed', reason: `The mail sink could not be read: ${view.error ?? 'no reason given'}.` };
  }

  const tallies = tallyMail(view, orderId);
  const counts = tallies.map((t) => `${t.event} (${t.template}) ×${t.messages}`);
  const said = counts.length === 0 ? 'no message for this run yet' : counts.join(', ');

  const doubled = tallies.filter((t) => t.messages > 1);
  if (doubled.length > 0) {
    const list = doubled.map((t) => `${t.event} (${t.template}) sent ${t.messages} times`).join(', ');
    return { kind: 'failed', reason: `A message was sent more than once: ${list}. Counted: ${said}.` };
  }

  const unexpected = tallies.filter((t) => !expected.includes(t.template));
  if (unexpected.length > 0) {
    const list = unexpected.map((t) => (t.template ? `${t.event} (${t.template})` : t.eventId)).join(', ');
    return { kind: 'failed', reason: `A message answered an event this run does not expect: ${list}. Counted: ${said}.` };
  }

  const missing = expected.filter((template) => !tallies.some((t) => t.template === template));
  if (missing.length > 0) {
    const list = missing.map((t) => `${mailEvent(t)} (${t})`).join(', ');
    return { kind: 'waiting', detail: `Not sent yet: ${list}. Counted: ${said}.` };
  }

  return { kind: 'reached', detail: `One message for each event the order raised: ${said}.` };
}

export type ReviewVerdict =
  | { kind: 'reached'; detail: string }
  | { kind: 'waiting'; detail: string }
  | { kind: 'failed'; reason: string };

/**
 * Whether Ordering has logged the order as flagged for review for `reason` (run-locally.md: `Order <id> flagged for review:
 * <reason>.`, found in Loki by the order id). The host reads Loki by the order's own id, so the lines are this order's;
 * `placedAt` (ms, 0 when unknown) only says how long after the place it was logged. A flag for another reason is not a
 * failure on its own: an order may be flagged more than once, so it is said and the wait goes on. Only a Loki that could
 * not be asked fails the step, because then nothing can be judged.
 */
export function reviewVerdict(view: ReviewFlagView, reason: string, placedAt: number): ReviewVerdict {
  if (!view.reachable) {
    return { kind: 'failed', reason: `Loki could not be read: ${view.error ?? 'no reason given'}.` };
  }

  const wanted = view.flags.find((f) => f.reason === reason);
  if (wanted) {
    const after = placedAt > 0 ? secondsBetween(new Date(placedAt).toISOString(), wanted.at) : null;
    return {
      kind: 'reached',
      detail: `Ordering logged "${wanted.line}" at ${wanted.at}${after === null ? '' : `, ${after} s after the order was placed`}.`,
    };
  }

  if (view.flags.length === 0) {
    return { kind: 'waiting', detail: 'Ordering has not logged this order as flagged for review yet.' };
  }

  const others = [...new Set(view.flags.map((f) => f.reason))].join(', ');
  return { kind: 'waiting', detail: `Ordering has flagged it for review as ${others}, not ${reason}; still waiting for ${reason}.` };
}

export type ReservationVerdict =
  | { kind: 'reached'; detail: string }
  | { kind: 'waiting'; detail: string }
  | { kind: 'failed'; reason: string };

/**
 * Whether Inventory holds the stock for the order (run-locally.md: the reservation read, `status: Reserved`). A 404 is the
 * reservation not yet made, and another status is the reservation not yet done with (`Pending`) or already past it; both are
 * said and waited out until the cap. Anything else, a 401, a 403 or a 5xx, or a 200 that is not a reservation, ends the step as the
 * platform answered it, since waiting cannot change it.
 */
export function reservationVerdict(status: number, body: string): ReservationVerdict {
  if (status === 404) {
    return { kind: 'waiting', detail: 'Inventory answered 404: it has made no reservation for the order yet.' };
  }
  if (status !== 200) return { kind: 'failed', reason: `Answered ${status}.` };

  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    parsed = null;
  }
  const seen = parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Record<string, unknown>)['status'] : undefined;
  if (typeof seen !== 'string') {
    return { kind: 'failed', reason: 'Answered 200, but not with a reservation the step can follow.' };
  }
  if (seen === 'Reserved') {
    return { kind: 'reached', detail: 'The reservation reads Reserved: Inventory holds the stock for the order.' };
  }
  return { kind: 'waiting', detail: `Last read: the reservation is ${seen}, not Reserved yet.` };
}

export type CarrierVerdict =
  | { kind: 'reached'; detail: string }
  | { kind: 'waiting'; detail: string }
  | { kind: 'failed'; reason: string };

/**
 * What the simulator's log must show of an order placed since `since` (ms), by the outcome its code scripts (run-locally.md):
 * `refused` one booking answered 422; `down` repeated bookings, each 503; `slow` repeated bookings of one shipment, each
 * 201; `strange` one booking answered 201 and then repeated polls of the feed. The order's own booking is the first one
 * logged since it was placed, and its retries are the entries that share its `shipmentId`. The log has no order id, so
 * an order placed to the same code at the same time by another run is not told apart (blueprint-backend#1074).
 */
export function carrierVerdict(
  outcome: CarrierOutcome,
  bookings: readonly CarrierEntry[],
  polls: readonly CarrierEntry[],
  since: number,
): CarrierVerdict {
  const mine = bookings.filter((b) => b.at >= since).sort((a, b) => a.at - b.at);
  if (mine.length === 0) {
    return { kind: 'waiting', detail: 'The carrier has not been asked to book this order yet.' };
  }

  const first = mine[0];
  const attempts = first.shipmentId === null ? [first] : mine.filter((b) => b.shipmentId === first.shipmentId);
  const wrong = attempts.find((a) => a.status !== EXPECTED_STATUS[outcome]);
  if (wrong) {
    return {
      kind: 'failed',
      reason: `The carrier answered ${wrong.status} to a booking attempt, not ${EXPECTED_STATUS[outcome]}.`,
    };
  }

  const count = attempts.length;
  switch (outcome) {
    case 'refused':
      return { kind: 'reached', detail: 'The carrier refused the booking with 422.' };
    case 'down':
    case 'slow': {
      const said = outcome === 'down' ? 'answered 503' : 'answered 201 after Shipping had stopped waiting';
      return count >= 2
        ? { kind: 'reached', detail: `The carrier saw ${count} attempts at the same booking, each ${said}.` }
        : { kind: 'waiting', detail: `The carrier has seen ${count} attempt, ${said}; Shipping backs off before it tries again.` };
    }
    default: {
      const after = polls.filter((p) => p.at > first.at).length;
      return after >= 2
        ? { kind: 'reached', detail: `The carrier booked the parcel once (201) and its feed has been polled ${after} times since.` }
        : { kind: 'waiting', detail: `The carrier booked the parcel (201); its feed has been polled ${after} time(s) since.` };
    }
  }
}

/** The status each outcome's bookings are answered with (run-locally.md's carrier log). */
const EXPECTED_STATUS: Record<CarrierOutcome, number> = {
  refused: 422,
  down: 503,
  slow: 201,
  strange: 201,
};

/** Whether a refused cancel's body is the platform's `order.already_shipped` problem (a `code` member). */
export function isAlreadyShipped(status: number, body: string): boolean {
  if (status !== ALREADY_SHIPPED.status) return false;
  try {
    const parsed: unknown = JSON.parse(body);
    return (
      !!parsed &&
      typeof parsed === 'object' &&
      (parsed as Record<string, unknown>)['code'] === ALREADY_SHIPPED.code
    );
  } catch {
    return false;
  }
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
  if (key === 'held') {
    // run-locally.md's carrier table: a refusal, an outage, a slow carrier and a strange feed each leave the order confirmed.
    // Not a wait: the order is read once the carrier has been seen, and it is confirmed and not despatched or it is not.
    return read.status === 'confirmed' && read.timeline.dispatched === null
      ? { kind: 'reached', at: read.timeline.confirmed ?? read.asOf }
      : { kind: 'failed', reason: `The order reads ${read.status}, not confirmed: this script's cause should leave it there.` };
  }

  if (key === 'unchanged') {
    // Not a wait: the cancel was refused, so the order is still where the script left it, despatched and never cancelled.
    return read.status === 'dispatched' && read.timeline.cancelled === null
      ? { kind: 'reached', at: read.timeline.dispatched ?? read.asOf }
      : { kind: 'failed', reason: `The order reads ${read.status}, not dispatched: the refused cancel should have moved nothing.` };
  }

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

/** One successful order read of a run, with the console's own clock beside the platform's, so the strip can say when it last looked. */
export interface SagaRead {
  /** `Date.now()` when the read answered: the console's clock, the only one that says how long ago the strip looked. */
  readAt: number;
  status: string;
  /** The BFF's `asOf`: when it knew the order so, by the platform's clock. */
  asOf: string;
  timeline: Record<TimelineStep, string | null>;
  /** The saga's own word on the order, read straight after the BFF's; null where the catalog has no such read. */
  saga: SagaWord | null;
}

/**
 * What Ordering's `GET /v1/orders/{id}/saga` said (blueprint-backend `SagaStateView`). A 404 is no row, which
 * the backend's §9.6 reads as a finalised saga or an order it never saw, alike; any other answer is unreadable and
 * is shown as what it was, never as a state.
 */
export type SagaWord =
  | { kind: 'row'; state: string; startedAt: string; cancelReason: string | null }
  | { kind: 'none' }
  | { kind: 'unreadable'; status: number | null };

/** The saga read's answer for a status and a body. */
export function sagaWord(status: number, body: string): SagaWord {
  if (status === 404) return { kind: 'none' };
  if (status !== 200) return { kind: 'unreadable', status };
  try {
    const parsed: unknown = JSON.parse(body);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return { kind: 'unreadable', status };
    const o = parsed as Record<string, unknown>;
    const reason = o['cancelReason'];
    if (typeof o['currentState'] !== 'string' || typeof o['startedAt'] !== 'string') return { kind: 'unreadable', status };
    if (reason !== null && reason !== undefined && typeof reason !== 'string') return { kind: 'unreadable', status };
    return { kind: 'row', state: o['currentState'], startedAt: o['startedAt'], cancelReason: reason ?? null };
  } catch {
    return { kind: 'unreadable', status };
  }
}

/**
 * Where the saga's state and the buyer's status meet, copied from blueprint-backend
 * `docs/backend-architecture/09-messaging.md` §9.6, *Reading the saga's state*: the saga state (null is no row),
 * the buyer statuses that are its own at rest, and those that can run ahead of the saga until it consumes the
 * event. The two consume each event on their own clock. Read as vocabulary, a pair in either list; anything else
 * is a difference of fact. A drift test reads the backend's table against this one.
 */
export const SAGA_PAIRS: readonly (readonly [string | null, readonly string[], readonly string[]])[] = [
  ['AwaitingStock', ['placed'], ['cancelled']],
  ['AwaitingPayment', ['placed'], ['cancelled']],
  ['AwaitingConfirmation', ['placed'], ['confirmed', 'dispatched', 'cancelled']],
  ['Confirmed', ['confirmed'], ['dispatched', 'cancelled']],
  ['Compensating', ['placed', 'confirmed'], ['cancelled', 'out_of_stock', 'declined']],
  [null, ['dispatched', 'delivered', 'cancelled', 'out_of_stock', 'declined', 'placed'], ['confirmed']],
];

/** The saga's word in words, for the strip and the export. */
export function sagaText(word: SagaWord): string {
  switch (word.kind) {
    case 'row':
      return word.state;
    case 'none':
      return 'no row (a finalised saga, or an order it never saw)';
    case 'unreadable':
      return `unreadable (${word.status === null ? 'no answer' : `answered ${word.status}`})`;
  }
}

/** What a pairing means, in the words the strip and the export both use. */
export function pairingText(pairing: Pairing): string {
  switch (pairing) {
    case 'agree':
      return "The two agree: the same state in the saga's words and the buyer's.";
    case 'ahead':
      return "The buyer's status is ahead of the saga's state, which the backend expects until the saga has consumed the same event. If it stays so, the stuck-saga runbook is the next read.";
    case 'differs':
      return "The saga's state and the buyer's status are a pair the backend's table does not allow: a difference of fact, in the saga or in the BFF.";
    case 'unknown':
      return "This pair is not in the backend's table, so nothing is claimed about it.";
  }
}

/**
 * `agree`: the saga and the buyer say the same thing in their two vocabularies. `ahead`: the buyer's status is
 * one the saga has yet to catch up to, which the backend names as expected for a while. `differs`: a pair the
 * table does not allow, a fact to chase (the backend's stuck-saga runbook). `unknown`: a saga state the table
 * does not name, or a read that gave no state, so nothing is claimed.
 */
export type Pairing = 'agree' | 'ahead' | 'differs' | 'unknown';

export function pairing(saga: SagaWord | null, buyer: string): Pairing {
  if (saga === null || saga.kind === 'unreadable') return 'unknown';
  const row = SAGA_PAIRS.find(([state]) => state === (saga.kind === 'row' ? saga.state : null));
  if (!row) return 'unknown';
  if (row[1].includes(buyer)) return 'agree';
  return row[2].includes(buyer) ? 'ahead' : 'differs';
}

/** How long a status may stay as it is, across reads, before the strip calls the saga stalled; no saga step of the fakes or of `run-locally.md`'s happy path takes this long. */
export const STALL_AFTER_MS = 30_000;

/** The statuses the saga does not leave, so an unchanged read of one is an end and not a stall. */
const SETTLED: readonly string[] = ['delivered', ...CANCELLATIONS];

export interface SagaTransition {
  from: TimelineStep;
  to: TimelineStep;
  /** Seconds between the two stamps the order read carries; null where either is unreadable. */
  seconds: number | null;
}

/** What the strip shows: the last state read and when, the time each transition took, and whether the state has stood still. */
export interface SagaStrip {
  status: string;
  readAt: number;
  asOf: string;
  reads: number;
  transitions: SagaTransition[];
  /** From the first of the trailing reads that show this status to the last, on the console's clock. */
  unchangedForMs: number;
  stalled: boolean;
  /** The platform's stamp for the step the buyer's status is at, or null where the order read carries none. */
  statusChangedAt: string | null;
  /** The saga's word at the last read; null where the catalog has no saga read. */
  saga: SagaWord | null;
  /** When the console first saw the saga in this state (or no row), in the trailing reads: the backend stores no time of entering a state. */
  sagaSeenAt: number | null;
  pairing: Pairing | null;
}

/** The step of an order read's timeline that a buyer status stands at: the two refusals are cancellations. */
function stepOf(status: string): TimelineStep | null {
  if (CANCELLATIONS.includes(status)) return 'cancelled';
  return (TIMELINE_STEPS as readonly string[]).includes(status) ? (status as TimelineStep) : null;
}

/** Whether two reads have the saga in the same place: the same state, or both with no row. */
function sameSaga(a: SagaWord | null, b: SagaWord | null): boolean {
  if (a === null || b === null) return a === b;
  if (a.kind !== b.kind) return false;
  return a.kind !== 'row' || (b.kind === 'row' && a.state === b.state);
}

/** The strip for a run's reads, oldest first, or null before the first one. A transition's time is the platform's two stamps, never the console's polls. */
export function sagaStrip(reads: readonly SagaRead[]): SagaStrip | null {
  const last = reads.at(-1);
  if (!last) return null;

  let first = last;
  for (let i = reads.length - 2; i >= 0 && reads[i].status === last.status; i--) first = reads[i];

  const stamped = TIMELINE_STEPS.filter((step) => last.timeline[step] !== null);
  const transitions: SagaTransition[] = [];
  for (let i = 1; i < stamped.length; i++) {
    transitions.push({
      from: stamped[i - 1],
      to: stamped[i],
      seconds: secondsBetween(last.timeline[stamped[i - 1]], last.timeline[stamped[i]]!),
    });
  }

  let sagaFirst = last;
  for (let i = reads.length - 2; i >= 0 && sameSaga(reads[i].saga, last.saga); i--) sagaFirst = reads[i];

  const step = stepOf(last.status);
  const unchangedForMs = last.readAt - first.readAt;
  return {
    statusChangedAt: step === null ? null : last.timeline[step],
    saga: last.saga,
    // An unreadable answer saw no state, so it has no "seen so" to claim.
    sagaSeenAt: last.saga === null || last.saga.kind === 'unreadable' ? null : sagaFirst.readAt,
    pairing: last.saga === null ? null : pairing(last.saga, last.status),
    status: last.status,
    readAt: last.readAt,
    asOf: last.asOf,
    reads: reads.length,
    transitions,
    unchangedForMs,
    stalled: unchangedForMs >= STALL_AFTER_MS && !SETTLED.includes(last.status),
  };
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
