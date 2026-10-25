import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { EMPTY, Subscription, catchError, exhaustMap, map, takeUntil, takeWhile, tap, timer } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import {
  ApiCatalogView,
  ApiOperation,
  OpenApiChanges,
  ProjectionDrain,
  ProxyRequest,
  ProxyResult,
  TokenClockView,
  TokenView,
} from '../../../core/host/host-types';
import { IdentityState } from '../../../core/identity/identity-state';
import { DrainedIndicator } from '../../../shared/drained-indicator/drained-indicator';
import { injectWidgetContext } from '../../../workspace/widget-context';
import { widgetSession } from '../../../workspace/widget-session-store';
import { WorkspaceService } from '../../../workspace/workspace-service';
import { TraceFollow } from '../../trace/trace-follow';
import {
  ApiHistory,
  CUSTOM,
  DRAIN_POLL_MS,
  DRAIN_WATCH_MS,
  HistoryEntry,
  PUBLISH_OPERATION,
  UUID,
  clockText,
  redact,
} from '../api-client';
import {
  buildUrl,
  parseHeaders,
  pretty,
  withFreshCommandId,
  withoutCredentialHeaders,
  withoutCredentialLines,
  withoutSetCookie,
  withPathValues,
} from '../request-builder';

/**
 * One API request (spec §5.5; widget-workspace spec §5.2): the catalog of the platform's operations, an editor, the
 * identity it is sent as, and the answer. Config: `operation`, the operation id it starts on, and `identity`
 * (`anonymous`, `user:<name>` or `custom:<name>`), which is the username only: a custom identity's password is session
 * state and is never written to the workspace (§6.4). What it sends is kept by `ApiHistory`, for the API history widget.
 */
@Component({
  selector: 'app-api-request-widget',
  imports: [DatePipe, FormsModule, DrainedIndicator],
  templateUrl: './api-request-widget.html',
  styleUrl: './api-request-widget.css',
})
export class ApiRequestWidget {
  private readonly host = inject(HostClient);
  private readonly uuid = inject(UUID);
  private readonly follow = inject(TraceFollow);
  private readonly workspace = inject(WorkspaceService);
  private readonly context = injectWidgetContext();
  private readonly apiHistory = inject(ApiHistory);
  /** Sends still in flight. They are not cancelled when the editor moves on: the platform may already have acted on one. */
  private readonly sends = new Set<Subscription>();
  /** Bumped when the editor is replaced (select, restore); a send finishing under an older value only reaches history. */
  private editorGeneration = 0;
  /** A restored entry's commandId handling, used when its operation is no longer in the catalog. */
  private restoredHasCommandId = false;
  private fetchingToken?: Subscription;
  private watchingProjection?: Subscription;

  readonly config = input<Record<string, unknown>>({});

  /** Accessible names with the operation's own words in them, in the active language. */
  pathLabel = (name: string): string => $localize`Path ${name}:name:`;
  queryLabel = (name: string): string => $localize`Query ${name}:name:`;
  traceLabel = (id: string): string => $localize`Trace correlation id ${id}:id:`;
  readonly identity = inject(IdentityState);
  /** A custom identity's password, kept for the tab and never saved: it is not in the config, only here. */
  private readonly savedPassword = widgetSession('customPassword', '');

  readonly catalog = signal<ApiCatalogView | null>(null);
  // The editor is session state (§4.2): what the operator set up and the last answer outlive the component, so leaving
  // the page and coming back finds the request as it was. A send in flight does not (the platform may have acted on it;
  // its answer reaches the history). The headers are kept as typed, an Authorization line included, in the tab's memory
  // only (the history strips such lines before it keeps a request); the custom password is held apart and never saved.
  readonly selectedId = widgetSession<string | null>('selectedId', null);
  readonly method = widgetSession('method', 'GET');
  readonly url = widgetSession('url', '');
  readonly pathValues = widgetSession<Record<string, string>>('pathValues', {});
  readonly queryValues = widgetSession<Record<string, string>>('queryValues', {});
  readonly headersText = widgetSession('headersText', '');
  readonly body = widgetSession('body', '');
  readonly correlationId = widgetSession('correlationId', '');
  readonly pending = signal(false);
  readonly result = widgetSession<ProxyResult | null>('result', null);
  /** The request the shown result answers (method, URL, identity), since the inputs may have changed since. */
  readonly answered = widgetSession('answered', '');
  readonly token = signal<TokenView | null>(null);
  /** The selected identity's token as the host holds it, read without minting (spec §5.6); null for anonymous. */
  readonly clock = signal<TokenClockView | null>(null);
  /** Ticks the clock's remaining time; the times themselves come from the host. */
  private readonly now = toSignal(timer(0, 1000).pipe(map(() => Date.now())), { initialValue: Date.now() });
  readonly clockText = computed(() => clockText(this.clock(), this.now()));
  private readingClock?: Subscription;
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

  /** The identity as the config says it: `anonymous`, `user:<name>` or `custom:<name>`; the username, never the password. */
  readonly identityConfigValue = computed(() => {
    const c = this.identity.choice();
    return c.kind === 'anonymous' ? 'anonymous' : c.kind === 'user' ? `user:${c.username}` : `custom:${c.username}`;
  });

  private readonly configuredIdentity = computed(() => {
    const value = this.config()['identity'];
    return typeof value === 'string' && /^(anonymous|user:.+|custom:.*)$/.test(value) ? value : null;
  });
  private readonly configuredOperation = computed(() => {
    const value = this.config()['operation'];
    return typeof value === 'string' && value !== '' ? value : null;
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
    return r?.outcome === 'responded'
      ? Object.entries(r.headers).flatMap(([name, values]) => values.map((value) => `${name}: ${value}`))
      : [];
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    this.apiHistory.editors.update((n) => n + 1);
    destroyRef.onDestroy(() => {
      this.apiHistory.editors.update((n) => n - 1);
      this.watchingProjection?.unsubscribe();
      this.sends.forEach((send) => send.unsubscribe());
      this.fetchingToken?.unsubscribe();
      this.readingClock?.unsubscribe();
      // The choice outlives the widget, a custom password does not stay in the shared identity state: it is in this
      // widget's session state, for the tab, and nowhere else. Another request widget still showing is using it, so
      // only the last one to go takes it out.
      const c = this.identity.choice();
      if (c.kind === CUSTOM && this.apiHistory.editors() === 0) this.identity.select({ ...c, password: '' });
    });
    // Refill the inputs from a retained custom choice, so they show what will be sent.
    const chosen = this.identity.choice();
    if (chosen.kind === CUSTOM) {
      this.customUsername.set(chosen.username);
      this.customPassword.set(chosen.password || this.savedPassword());
      if (!chosen.password && this.savedPassword()) this.identity.select({ ...chosen, password: this.savedPassword() });
    }
    this.identity.load();

    // The identity the config names is taken up when it differs from the one in use: on appearing, and when the
    // settings editor or a preset changes it. A change this widget made itself is already in use, so it does nothing.
    effect(() => {
      const wanted = this.configuredIdentity();
      untracked(() => {
        if (wanted !== null && wanted !== this.identityConfigValue()) this.applyIdentity(wanted);
      });
    });
    // The clock follows the selected identity, including the default the user list picks once it loads. A custom
    // identity's is read after it is used, not on every keystroke of its password.
    effect(() => {
      const custom = this.identity.choice().kind === CUSTOM;
      untracked(() => {
        if (custom) {
          this.readingClock?.unsubscribe();
          this.clock.set(null);
        } else {
          this.readClock();
        }
      });
    });
    // The operation the config names is selected once the catalog has it, and again when the config comes to name
    // another (the settings editor, a preset). One the operator picked here is already in the config, so it is a no-op.
    // Taken up once per operation the config names: a catalog reload, or a restore that moved the editor elsewhere,
    // does not take the editor back to the configured one.
    let applied: string | null = null;
    // An editor found in session state when the widget appeared is the operator's own work: the configured operation
    // does not replace it, though a config that later names another does.
    let found = this.selectedId() !== null || this.url() !== '' || this.body() !== '' || this.result() !== null;
    effect(() => {
      const view = this.catalog();
      const wanted = this.configuredOperation();
      untracked(() => {
        if (found) {
          found = false;
          applied = wanted;
          return;
        }
        if (!wanted || wanted === applied) return;
        if (this.selectedId() === wanted) {
          applied = wanted;
          return;
        }
        const operation = view?.operations.find((o) => o.id === wanted);
        if (operation) {
          applied = wanted;
          this.select(operation, false);
        }
      });
    });
    // A history entry asked back after this editor appeared; one asked before is not replayed into it.
    let handled = this.apiHistory.restoreRequest()?.n ?? 0;
    effect(() => {
      const request = this.apiHistory.restoreRequest();
      if (request && request.n > handled) {
        handled = request.n;
        untracked(() => this.restore(request.entry));
      }
    });
    this.host.operations().subscribe({
      next: (view) => this.catalog.set(view),
      error: (e: unknown) => this.error.set(this.describe(e)),
    });
  }

  /** The document's moves as lines an operator reads, operations first; empty when nothing moved. */
  changeLines(changes: OpenApiChanges): string[] {
    return [
      ...changes.operationsAdded.map((o) => `added operation ${o}`),
      ...changes.operationsRemoved.map((o) => `removed operation ${o}`),
      ...changes.statusesAdded.map((s) => `added response ${s}`),
      ...changes.statusesRemoved.map((s) => `removed response ${s}`),
      ...changes.fieldsAdded.map((f) => `added field ${f}`),
      ...changes.fieldsRemoved.map((f) => `removed field ${f}`),
    ];
  }

  acceptBaseline(service: string): void {
    this.host.acceptBaseline(service).subscribe({
      next: (view) => {
        this.error.set(null);
        this.catalog.set(view);
      },
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

  /** `save` is false when the config itself asked for the operation, which is already saved. */
  select(operation: ApiOperation, save = true): void {
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
    if (save) this.saveConfig(false);
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
    this.saveConfig(true);
  }

  setCustom(username: string, password: string): void {
    this.clearToken();
    this.customUsername.set(username);
    this.customPassword.set(password);
    this.savedPassword.set(password);
    this.identity.select({ kind: 'custom', username, password });
    this.saveConfig(true);
  }

  /**
   * Saves what the config holds of this widget: the operation, and the identity by username. Never the password, which
   * is not a key of what is written (and the host refuses a config with a key named like one, §6.4).
   */
  private saveConfig(identityChosen: boolean): void {
    if (!this.context) return;
    const operation = this.selectedId();
    // The identity is written only when the operator chose one here: the one in use may be a default, or one the
    // Scenario widget picked, and freezing that into this widget would take it back over a later choice.
    const identity = identityChosen ? this.identityConfigValue() : this.configuredIdentity();
    // Nothing is saved that the config already says: a password typed a letter at a time saves nothing, for one.
    if (operation === this.configuredOperation() && identity === this.configuredIdentity()) return;
    this.workspace.configureWidget(this.context.pageId, this.context.instanceId, { operation, identity });
  }

  /** Takes up an identity the config names: `anonymous`, `user:<name>` or `custom:<name>` with the tab's password. */
  private applyIdentity(value: string): void {
    this.clearToken();
    if (value === 'anonymous') {
      this.identity.select({ kind: 'anonymous' });
    } else if (value.startsWith('custom:')) {
      const username = value.slice('custom:'.length);
      this.customUsername.set(username);
      this.customPassword.set(this.savedPassword());
      this.identity.select({ kind: 'custom', username, password: this.savedPassword() });
    } else {
      this.identity.select({ kind: 'user', username: value.slice('user:'.length) });
    }
  }

  /** Reads the selected identity's clock from the host; anonymous has none. */
  readClock(): void {
    this.readingClock?.unsubscribe();
    const request = this.identity.request();
    if (!request) {
      this.clock.set(null);
      return;
    }
    this.readingClock = this.host.tokenClock(request).subscribe({
      next: (clock) => this.clock.set(clock),
      error: () => this.clock.set(null),
    });
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
      body: body.trim() ? withPathValues(body, this.pathValues()) : null,
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
          // The send may have minted or renewed the token, so the clock is read again.
          this.readClock();
          if (
            operation?.source === 'catalog' &&
            operation.name === PUBLISH_OPERATION &&
            result.outcome === 'responded' &&
            result.status >= 200 &&
            result.status < 300
          ) {
            this.watchProjection();
          }
        }
        this.apiHistory.add({
          name,
          operationId,
          hasCommandId,
          headersText: withoutCredentialLines(headersText),
          urlTemplate,
          pathValues,
          queryValues,
          bodyTemplate: body,
          identity,
          sentAs,
          request: { ...request, headers: withoutCredentialHeaders(request.headers), identity: null },
          result: withoutSetCookie(result),
        });
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
   * Opens this call's correlation id on a Trace widget that follows, or the Trace page when none does. Offered only
   * where something was actually sent: on `tokenRejected` no request left the console, so there is nothing to trace.
   */
  traceThisCall(correlationId: string): void {
    this.follow.open(correlationId);
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
    this.body.set(entry.bodyTemplate);
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
        this.readClock();
      },
      error: (e: unknown) => this.error.set(this.describe(e)),
    });
  }

  claims(token: TokenView): string {
    return JSON.stringify(token.claims, null, 2);
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
    const err = e as {
      error?: { detail?: string; title?: string; error_description?: string };
      message?: string;
    } | null;
    return (
      err?.error?.detail ??
      err?.error?.error_description ??
      err?.error?.title ??
      err?.message ??
      'The host did not answer.'
    );
  }
}
