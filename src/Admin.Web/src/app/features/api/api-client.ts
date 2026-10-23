import { Injectable, InjectionToken, computed, signal } from '@angular/core';
import { ProxyRequest, ProxyResult, TokenClockView } from '../../core/host/host-types';
import { IdentityChoice } from '../../core/identity/identity-state';

export const UUID = new InjectionToken<() => string>('UUID', { factory: () => () => crypto.randomUUID() });

export interface HistoryEntry {
  seq: number;
  at: Date;
  name: string;
  /** The operation selected when sent, so a restored entry resends with that operation's commandId handling. */
  operationId: string | null;
  /** Whether Send mints a fresh commandId, kept so a restore still does after a reload drops the operation. */
  hasCommandId: boolean;
  headersText: string;
  /** The URL template and parameter values as entered, so a restored entry's inputs still build its URL. */
  urlTemplate: string;
  pathValues: Record<string, string>;
  queryValues: Record<string, string>;
  /** The body as typed, before its `{name}` placeholders were filled from `pathValues`, so a restore can change what they fill. */
  bodyTemplate: string;
  identity: string;
  /** Who it went out as, without a custom password: history is not a credential store, so a restore asks for it again. */
  sentAs: SentAs;
  /** As sent, with the identity and any credential header (Authorization, Cookie) left out: none is kept, so a restore asks again. */
  request: ProxyRequest;
  result: ProxyResult;
}

export type SentAs = { kind: 'anonymous' } | { kind: 'user'; username: string } | { kind: 'custom'; username: string };

export const MAX_HISTORY = 50;
export const CUSTOM = 'custom';

/**
 * Catalog's one write, `WithName("PublishProduct")` in the backend's Catalog.Api/Endpoints/ProductEndpoints.cs:
 * its event is what Ordering's price projection consumes from ordering-catalog-events (run-locally.md). Matched
 * together with `source === 'catalog'`, since another source could name an operation the same.
 */
export const PUBLISH_OPERATION = 'PublishProduct';
export const DRAIN_POLL_MS = 2000;
/** Each poll is a docker compose exec on the host; a queue that has not drained in two minutes needs the Broker screen. */
export const DRAIN_WATCH_MS = 120_000;

/**
 * The calls this tab has sent from an API request widget (widget-workspace spec §4.2, §5.2), newest first and bounded.
 * Root-provided, so the API history widget on the page lists what the API request widget beside it sent, and what was
 * sent survives the page being left. It is memory only, as spec §8 keeps the console's state: no credential is in an
 * entry (`HistoryEntry`), and nothing here is saved to the workspace.
 */
@Injectable({ providedIn: 'root' })
export class ApiHistory {
  private seq = 0;

  readonly entries = signal<HistoryEntry[]>([]);

  /** How many API request widgets are showing; a history entry is restored into one of them, and offers it only while one is. */
  readonly editors = signal(0);

  /** The entry a history widget asked an editor to take back, counted so the same entry asked twice is two requests. */
  readonly restoreRequest = signal<{ entry: HistoryEntry; n: number } | null>(null);

  readonly canRestore = computed(() => this.editors() > 0);

  /** Records a call; the entry's `seq` is this service's, newest first, and the oldest beyond `MAX_HISTORY` goes. */
  add(entry: Omit<HistoryEntry, 'seq' | 'at'>): void {
    this.entries.update((all) => [{ ...entry, seq: ++this.seq, at: new Date() }, ...all].slice(0, MAX_HISTORY));
  }

  requestRestore(entry: HistoryEntry): void {
    this.restoreRequest.update((last) => ({ entry, n: (last?.n ?? 0) + 1 }));
  }

  /** A short line for one entry: how it ended, what it was, and who sent it. */
  label(entry: HistoryEntry): string {
    const r = entry.result;
    const outcome =
      r.outcome === 'responded' ? String(r.status) : r.outcome === 'tokenRejected' ? `token ${r.status}` : 'unreached';
    return `${outcome} ${entry.name} as ${entry.identity}`;
  }
}

/**
 * The clock as the picker prints it, at `now`: how long the held token has left, when the next call stops reusing
 * it (the host's renewal time, not a threshold of the page's), and what it grants, which is what explains a 403.
 */
export function clockText(clock: TokenClockView | null, now: number): string | null {
  if (!clock?.username) return null;
  if (!clock.held || !clock.expiresAt || !clock.renewsAt)
    return `No token held for ${clock.username}; the next call mints one.`;
  const left = Date.parse(clock.expiresAt) - now;
  const renews = Date.parse(clock.renewsAt) - now;
  const grants = clock.permissions.length ? `Grants ${clock.permissions.join(', ')}.` : 'Grants no permission.';
  if (left <= 0) return `${clock.username}'s token has expired; the next call mints one. ${grants}`;
  const when = renews > 0 ? `the next call reuses it for ${duration(renews)} more` : 'the next call mints a new one';
  return `${clock.username}'s token: ${duration(left)} left, and ${when}. ${grants}`;
}

function duration(ms: number): string {
  const seconds = Math.ceil(ms / 1000);
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

export function redact(choice: IdentityChoice): SentAs {
  return choice.kind === 'custom' ? { kind: 'custom', username: choice.username } : choice;
}
