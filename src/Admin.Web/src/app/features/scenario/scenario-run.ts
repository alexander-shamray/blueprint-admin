import { ApiCatalogView, ApiOperation } from '../../core/host/host-types';
import { PUBLISH_OPERATION } from '../api/api-page';

/** The five steps of run-locally.md's "Call the APIs", in the order that document gives them. */
export type StepKey = 'publish' | 'drain' | 'quote' | 'order' | 'cancel';

export const STEP_KEYS: readonly StepKey[] = ['publish', 'drain', 'quote', 'order', 'cancel'];

export const STEP_TITLES: Record<StepKey, string> = {
  publish: 'Publish a product',
  drain: 'Wait for the projection',
  quote: 'Quote a basket',
  order: 'Place an order',
  cancel: 'Cancel it',
};

/**
 * The catalog operation each HTTP step sends, by the id the host gives it: `<source>:<operationId>`
 * for the OpenAPI ones (the backend's `WithName` in ProductEndpoints.cs and OrderEndpoints.cs) and
 * the curated BFF quote (Admin.Host `CuratedOperations`). The body is that operation's example,
 * which `RunLocallyExamples` owns.
 */
export const STEP_OPERATIONS: Record<Exclude<StepKey, 'drain'>, string> = {
  publish: `catalog:${PUBLISH_OPERATION}`,
  quote: 'bff:Quote',
  order: 'ordering:PlaceOrder',
  cancel: 'ordering:CancelOrder',
};

export type ScenarioOperations = Record<Exclude<StepKey, 'drain'>, ApiOperation>;

/**
 * Every operation the run needs, or the reasons it cannot start: one missing from the catalog, or
 * listed as unavailable because its service's document did not load.
 */
export function resolveOperations(view: ApiCatalogView): {
  operations: ScenarioOperations | null;
  missing: string[];
} {
  const found: Partial<ScenarioOperations> = {};
  const missing: string[] = [];
  for (const [key, id] of Object.entries(STEP_OPERATIONS) as [keyof ScenarioOperations, string][]) {
    const operation = view.operations.find((o) => o.id === id);
    if (!operation) {
      missing.push(`${id} is not in the operation catalog`);
    } else if (!operation.available) {
      const error = view.sources.find((s) => s.name === operation.source)?.error;
      const reason = `${id} is unavailable: its service's document did not load`;
      missing.push(error ? `${reason}: ${error}` : reason);
    } else {
      found[key] = operation;
    }
  }
  return { operations: missing.length === 0 ? (found as ScenarioOperations) : null, missing };
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
