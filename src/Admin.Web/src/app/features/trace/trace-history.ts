import { Injectable } from '@angular/core';
import { TraceEvent, TraceView } from '../../core/host/host-types';

/** How many fetches of one correlation id are kept, and how many ids. Both bound the memory a long session holds. */
export const KEPT_PER_ID = 5;
export const KEPT_IDS = 20;

/** One fetch of a timeline, with when the page received it. */
export interface TraceFetch {
  fetchedAt: Date;
  view: TraceView;
}

/**
 * What moved between two fetches of one id. `comparable` is false when the window differs, because an
 * event can leave or enter a timeline only by the window moving and that is not news about the flow.
 */
export interface TraceDiff {
  previousAt: Date;
  currentAt: Date;
  comparable: boolean;
  previousWindow: string;
  arrived: TraceEvent[];
  gone: TraceEvent[];
}

/**
 * The last few timelines per correlation id, for the browser session (spec §5.9). Root-provided so
 * leaving the Trace screen and coming back keeps them; never written to storage, because spec §8 keeps
 * the console's state in memory.
 */
@Injectable({ providedIn: 'root' })
export class TraceHistory {
  /** Insertion order is recency: an id is moved to the end whenever it is fetched, and the first is dropped first. */
  private readonly byId = new Map<string, TraceFetch[]>();

  /** Records a fetch and returns the one before it for the same id, if this session has one. */
  record(view: TraceView, fetchedAt: Date): TraceFetch | null {
    const fetches = this.byId.get(view.correlationId) ?? [];
    const previous = fetches.at(-1) ?? null;

    fetches.push({ fetchedAt, view });
    if (fetches.length > KEPT_PER_ID) fetches.shift();

    this.byId.delete(view.correlationId);
    this.byId.set(view.correlationId, fetches);

    while (this.byId.size > KEPT_IDS) {
      const oldest = this.byId.keys().next().value as string;
      this.byId.delete(oldest);
    }

    return previous;
  }

  fetches(correlationId: string): readonly TraceFetch[] {
    return this.byId.get(correlationId) ?? [];
  }
}

/**
 * An event's identity across fetches: everything it says, since the host gives events no id of their own.
 * The terminal `Queued` row is the exception on `at`: it is the broker snapshot, taken anew on every fetch,
 * so its time always moves and only its summary — the queue and its depth — says whether anything did.
 */
export function eventKey(event: TraceEvent): string {
  const at = event.kind === 'Queued' ? '' : event.at;
  return [at, event.source, event.service, event.kind, event.summary].join('\u0000');
}

/** The events that arrived and left between two fetches, over what the screen already read. */
export function diffTimelines(previous: TraceFetch, current: TraceFetch): TraceDiff {
  const before = new Set(previous.view.events.map(eventKey));
  const after = new Set(current.view.events.map(eventKey));
  const comparable = previous.view.window === current.view.window;

  return {
    previousAt: previous.fetchedAt,
    currentAt: current.fetchedAt,
    comparable,
    previousWindow: previous.view.window,
    arrived: comparable ? current.view.events.filter((e) => !before.has(eventKey(e))) : [],
    gone: comparable ? previous.view.events.filter((e) => !after.has(eventKey(e))) : [],
  };
}
