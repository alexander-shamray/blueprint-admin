import { Component, DestroyRef, InjectionToken, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { EMPTY, Subscription, catchError, exhaustMap, takeUntil, takeWhile, tap, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ApiCatalogView, ApiOperation, ProjectionDrain, ProxyRequest, ProxyResult, TokenView } from '../../core/host/host-types';
import { IdentityChoice, IdentityState } from '../../core/identity/identity-state';
import { buildUrl, parseHeaders, pretty, withFreshCommandId, withoutCredentialHeaders, withoutCredentialLines, withoutSetCookie } from './request-builder';
import { DrainedIndicator } from '../../shared/drained-indicator/drained-indicator';

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
  identity: string;
  /** Who it went out as, without a custom password: history is not a credential store, so a restore asks for it again. */
  sentAs: SentAs;
  /** As sent, with the identity and any credential header (Authorization, Cookie) left out: none is kept, so a restore asks again. */
  request: ProxyRequest;
  result: ProxyResult;
}

export type SentAs = { kind: 'anonymous' } | { kind: 'user'; username: string } | { kind: 'custom'; username: string };

const MAX_HISTORY = 50;
const CUSTOM = 'custom';

/**
 * Catalog's one write, `WithName("PublishProduct")` in the backend's Catalog.Api/Endpoints/ProductEndpoints.cs:
 * its event is what Ordering's price projection consumes from ordering-catalog-events (run-locally.md). Matched
 * together with `source === 'catalog'`, since another source could name an operation the same.
 */
export const PUBLISH_OPERATION = 'PublishProduct';
export const DRAIN_POLL_MS = 2000;
/** Each poll is a docker compose exec on the host; a queue that has not drained in two minutes needs the Broker screen. */
export const DRAIN_WATCH_MS = 120_000;

@Component({
  selector: 'app-api-page',
  imports: [FormsModule, DrainedIndicator],
  templateUrl: './api-page.html',
  styleUrl: './api-page.css',
})
export class ApiPage {
  private readonly host = inject(HostClient);
  private readonly uuid = inject(UUID);
  /** Sends still in flight. They are not cancelled when the editor moves on: the platform may already have acted on one. */
  private readonly sends = new Set<Subscription>();
  /** Bumped when the editor is replaced (select, restore); a send finishing under an older value only reaches history. */
  private editorGeneration = 0;
  /** A restored entry's commandId handling, used when its operation is no longer in the catalog. */
  private restoredHasCommandId = false;
  private fetchingToken?: Subscription;
  private seq = 0;
  private watchingProjection?: Subscription;

  readonly identity = inject(IdentityState);
  readonly catalog = signal<ApiCatalogView | null>(null);
  readonly selectedId = signal<string | null>(null);
  readonly method = signal('GET');
  readonly url = signal('');
  readonly pathValues = signal<Record<string, string>>({});
  readonly queryValues = signal<Record<string, string>>({});
  readonly headersText = signal('');
  readonly body = signal('');
  readonly correlationId = signal('');
  readonly pending = signal(false);
  readonly result = signal<ProxyResult | null>(null);
  /** The request the shown result answers (method, URL, identity), since the inputs may have changed since. */
  readonly answered = signal('');
  readonly history = signal<HistoryEntry[]>([]);
  readonly token = signal<TokenView | null>(null);
  readonly error = signal<string | null>(null);
  readonly customUsername = signal('');
  readonly customPassword = signal('');
  /** ordering-catalog-events after a successful publish, until it drains; null when not watching. */
  readonly projection = signal<ProjectionDrain | null>(null);
  readonly projectionError = signal<string | null>(null);
  /** Set when the two-minute cap, not the queue draining, ended the watch; reset when a new watch starts or stops. */
  readonly projectionWatchExpired = signal(false);

  readonly methods = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'];

  readonly operation = computed(() => this.catalog()?.operations.find((o) => o.id === this.selectedId()) ?? null);

  readonly groups = computed(() => {
    const view = this.catalog();
    if (!view) return [];
    const names = [...new Set([...view.sources.map((s) => s.name), ...view.operations.map((o) => o.source)])];
    return names.map((name) => ({
      name,
      source: view.sources.find((s) => s.name === name) ?? null,
      operations: view.operations.filter((o) => o.source === name),
    }));
  });

  /** The identity `<select>`'s value: `anonymous`, `user:<name>` or `custom`. */
  readonly identityValue = computed(() => {
    const c = this.identity.choice();
    return c.kind === 'user' ? `user:${c.username}` : c.kind;
  });

  readonly prettyBody = computed(() => {
    const r = this.result();
    return r && r.outcome !== 'unreached' ? pretty(r.body) : '';
  });

  /** A short line for the polite live region, so a screen reader hears that the request finished. */
  readonly announcement = computed(() => {
    const r = this.result();
    if (!r) return '';
    switch (r.outcome) {
      case 'responded':
        return `Response ${r.status} in ${r.elapsedMs} ms`;
      case 'tokenRejected':
        return `Keycloak refused the identity: ${r.status}`;
      case 'unreached':
        return 'No answer from the platform';
    }
  });

  readonly headerLines = computed(() => {
    const r = this.result();
    // One line per value: fields such as Set-Cookie cannot be joined with commas.
    return r?.outcome === 'responded' ? Object.entries(r.headers).flatMap(([name, values]) => values.map((value) => `${name}: ${value}`)) : [];
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.watchingProjection?.unsubscribe();
      this.sends.forEach((send) => send.unsubscribe());
      this.fetchingToken?.unsubscribe();
      // The choice outlives the screen, a custom password does not: passwords live only on the host.
      const c = this.identity.choice();
      if (c.kind === CUSTOM) this.identity.select({ ...c, password: '' });
    });
    // Refill the inputs from a retained custom choice, so they show what will be sent.
    const chosen = this.identity.choice();
    if (chosen.kind === CUSTOM) {
      this.customUsername.set(chosen.username);
      this.customPassword.set(chosen.password);
    }
    this.identity.load();
    this.host.operations().subscribe({
      next: (view) => this.catalog.set(view),
      error: (e: unknown) => this.error.set(this.describe(e)),
    });
  }

  reload(): void {
    this.host.reloadOperations().subscribe({
      next: (view) => {
        this.error.set(null);
        this.catalog.set(view);
      },
      error: (e: unknown) => this.error.set(this.describe(e)),
    });
  }

  select(operation: ApiOperation): void {
    this.editorGeneration++;
    this.stopWatchingProjection();
    this.pending.set(false);
    this.error.set(null);
    this.result.set(null);
    this.selectedId.set(operation.id);
    this.restoredHasCommandId = false;
    this.method.set(operation.method);
    this.url.set(operation.url);
    this.pathValues.set({});
    this.queryValues.set({});
    this.body.set(operation.exampleBody ?? '');
  }

  setPathValue(name: string, value: string): void {
    this.pathValues.update((all) => ({ ...all, [name]: value }));
  }

  setQueryValue(name: string, value: string): void {
    this.queryValues.update((all) => ({ ...all, [name]: value }));
  }

  chooseIdentity(value: string): void {
    this.clearToken();
    if (value === 'anonymous') {
      this.identity.select({ kind: 'anonymous' });
    } else if (value === CUSTOM) {
      this.identity.select({ kind: 'custom', username: this.customUsername(), password: this.customPassword() });
    } else {
      this.identity.select({ kind: 'user', username: value.slice('user:'.length) });
    }
  }

  setCustom(username: string, password: string): void {
    this.clearToken();
    this.customUsername.set(username);
    this.customPassword.set(password);
    this.identity.select({ kind: 'custom', username, password });
  }

  send(): void {
    // The last response stays in history; left on screen it would read as this attempt's.
    this.result.set(null);
    this.stopWatchingProjection();
    if (this.customWithoutUsername()) return;
    const operation = this.operation();
    const headersText = this.headersText();
    const { headers, invalid } = parseHeaders(headersText);
    if (invalid.length > 0) {
      this.error.set(`Not a "Name: value" header line, or a name given twice: ${invalid[0]}`);
      return;
    }

    let body = this.body();
    const hasCommandId = operation?.hasCommandId ?? this.restoredHasCommandId;
    if (hasCommandId && body.trim()) {
      body = withFreshCommandId(body, this.uuid());
      this.body.set(body);
    }

    const request: ProxyRequest = {
      method: this.method(),
      url: buildUrl(this.url(), this.pathValues(), this.queryValues()),
      headers,
      body: body.trim() ? body : null,
      identity: this.identity.request(),
      correlationId: this.correlationId().trim() || null,
    };
    const name = operation?.name ?? `${request.method} ${request.url}`;
    const operationId = operation?.id ?? null;
    const identity = this.identity.label();
    const sentAs = redact(this.identity.choice());
    const urlTemplate = this.url();
    const pathValues = this.pathValues();
    const queryValues = this.queryValues();

    this.error.set(null);
    this.pending.set(true);
    const generation = this.editorGeneration;
    const current = () => generation === this.editorGeneration;
    const send = this.host.proxy(request).subscribe({
      next: (result) => {
        if (current()) {
          this.result.set(result);
          this.answered.set(`${request.method} ${request.url} as ${identity}`);
          this.pending.set(false);
          if (operation?.source === 'catalog' && operation.name === PUBLISH_OPERATION && result.outcome === 'responded' && result.status >= 200 && result.status < 300) {
            this.watchProjection();
          }
        }
        this.history.update((all) => [{ seq: ++this.seq, at: new Date(), name, operationId, hasCommandId, headersText: withoutCredentialLines(headersText), urlTemplate, pathValues, queryValues, identity, sentAs, request: { ...request, headers: withoutCredentialHeaders(request.headers), identity: null }, result: withoutSetCookie(result) }, ...all].slice(0, MAX_HISTORY));
      },
      error: (e: unknown) => {
        // A refusal belongs to the editor that sent it; one that moved on has nothing to show it against.
        if (current()) {
          this.error.set(this.describe(e));
          this.pending.set(false);
        }
      },
    });
    if (!send.closed) {
      this.sends.add(send);
      send.add(() => this.sends.delete(send));
    }
  }

  /**
   * Puts a past request back as it was sent, identity included (a custom one without its password), with its
   * URL template and parameter values so the inputs shown are the ones that build the URL.
   */
  restore(entry: HistoryEntry): void {
    this.editorGeneration++;
    this.stopWatchingProjection();
    this.pending.set(false);
    this.error.set(null);
    this.result.set(entry.result);
    this.answered.set(`${entry.request.method} ${entry.request.url} as ${entry.identity}`);
    this.restoredHasCommandId = entry.hasCommandId;
    this.selectedId.set(entry.operationId);
    this.method.set(entry.request.method);
    this.url.set(entry.urlTemplate);
    this.pathValues.set(entry.pathValues);
    this.queryValues.set(entry.queryValues);
    this.headersText.set(entry.headersText);
    this.body.set(entry.request.body ?? '');
    this.correlationId.set(entry.request.correlationId ?? '');
    const sentAs = entry.sentAs;
    if (sentAs.kind === 'anonymous') {
      this.chooseIdentity('anonymous');
    } else if (sentAs.kind === 'user') {
      this.chooseIdentity(`user:${sentAs.username}`);
    } else {
      this.setCustom(sentAs.username, '');
    }
  }

  showToken(): void {
    if (this.customWithoutUsername()) return;
    const request = this.identity.request();
    this.clearToken();
    if (!request) {
      this.error.set('Anonymous has no token.');
      return;
    }
    this.fetchingToken = this.host.token(request).subscribe({
      next: (token) => {
        this.error.set(null);
        this.token.set(token);
      },
      error: (e: unknown) => this.error.set(this.describe(e)),
    });
  }

  claims(token: TokenView): string {
    return JSON.stringify(token.claims, null, 2);
  }

  historyLabel(entry: HistoryEntry): string {
    const r = entry.result;
    const outcome = r.outcome === 'responded' ? String(r.status) : r.outcome === 'tokenRejected' ? `token ${r.status}` : 'unreached';
    return `${outcome} ${entry.name} as ${entry.identity}`;
  }

  /** Hides the shown token and drops one still on its way, which belongs to the identity being left. */
  private clearToken(): void {
    this.fetchingToken?.unsubscribe();
    this.fetchingToken = undefined;
    this.token.set(null);
  }

  /**
   * Polls queues while the last answer was reachable, found and not drained; stops on drain, an
   * unreachable broker, an undeclared queue, or the two-minute cap, leaving the last view shown.
   */
  private watchProjection(): void {
    this.stopWatchingProjection();
    this.watchingProjection = timer(0, DRAIN_POLL_MS)
      .pipe(
        exhaustMap(() =>
          this.host.brokerQueues().pipe(
            catchError((e: unknown) => {
              this.projectionError.set(this.describe(e));
              this.projection.set(null);
              return EMPTY;
            }),
          ),
        ),
        // After exhaustMap, so the cap also unsubscribes a still-open brokerQueues() request instead of
        // just stopping the outer timer and leaving exhaustMap waiting on it forever.
        takeUntil(timer(DRAIN_WATCH_MS).pipe(tap(() => this.projectionWatchExpired.set(true)))),
        takeWhile((view) => view.reachable && view.projection.found && !view.projection.drained, true),
      )
      .subscribe((view) => {
        this.projectionError.set(view.reachable ? null : view.error);
        this.projection.set(view.reachable ? view.projection : null);
      });
  }

  private stopWatchingProjection(): void {
    this.watchingProjection?.unsubscribe();
    this.watchingProjection = undefined;
    this.projection.set(null);
    this.projectionError.set(null);
    this.projectionWatchExpired.set(false);
  }

  /** A custom identity with a blank username would go out as anonymous while history says "(custom)". */
  private customWithoutUsername(): boolean {
    const c = this.identity.choice();
    if (c.kind !== CUSTOM || c.username.trim()) return false;
    this.error.set('Enter a username for the custom identity.');
    return true;
  }

  /** A problem's `detail`/`title`, Keycloak's `error_description`, else the error's message. */
  private describe(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string; error_description?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.error_description ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}

function redact(choice: IdentityChoice): SentAs {
  return choice.kind === 'custom' ? { kind: 'custom', username: choice.username } : choice;
}
