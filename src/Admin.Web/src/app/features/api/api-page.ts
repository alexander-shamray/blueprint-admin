import { Component, DestroyRef, InjectionToken, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ApiCatalogView, ApiOperation, ProxyRequest, ProxyResult, TokenView } from '../../core/host/host-types';
import { IdentityState } from '../../core/identity/identity-state';
import { buildUrl, parseHeaders, pretty, withFreshCommandId } from './request-builder';

export const UUID = new InjectionToken<() => string>('UUID', { factory: () => () => crypto.randomUUID() });

export interface HistoryEntry {
  seq: number;
  at: Date;
  name: string;
  identity: string;
  request: ProxyRequest;
  result: ProxyResult;
}

const MAX_HISTORY = 50;
const CUSTOM = 'custom';

@Component({
  selector: 'app-api-page',
  imports: [FormsModule],
  templateUrl: './api-page.html',
  styleUrl: './api-page.css',
})
export class ApiPage {
  private readonly host = inject(HostClient);
  private readonly uuid = inject(UUID);
  private sending?: Subscription;
  private seq = 0;

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
  readonly history = signal<HistoryEntry[]>([]);
  readonly token = signal<TokenView | null>(null);
  readonly error = signal<string | null>(null);
  readonly customUsername = signal('');
  readonly customPassword = signal('');

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

  readonly headerLines = computed(() => {
    const r = this.result();
    return r?.outcome === 'responded' ? Object.entries(r.headers).map(([name, values]) => `${name}: ${values.join(', ')}`) : [];
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.sending?.unsubscribe());
    this.identity.load();
    this.host.operations().subscribe({
      next: (view) => this.catalog.set(view),
      error: (e: unknown) => this.error.set(this.describe(e)),
    });
  }

  reload(): void {
    this.host.reloadOperations().subscribe({
      next: (view) => this.catalog.set(view),
      error: (e: unknown) => this.error.set(this.describe(e)),
    });
  }

  select(operation: ApiOperation): void {
    this.sending?.unsubscribe();
    this.sending = undefined;
    this.pending.set(false);
    this.error.set(null);
    this.result.set(null);
    this.selectedId.set(operation.id);
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
    this.token.set(null);
    if (value === 'anonymous') {
      this.identity.select({ kind: 'anonymous' });
    } else if (value === CUSTOM) {
      this.identity.select({ kind: 'custom', username: this.customUsername(), password: this.customPassword() });
    } else {
      this.identity.select({ kind: 'user', username: value.slice('user:'.length) });
    }
  }

  setCustom(username: string, password: string): void {
    this.customUsername.set(username);
    this.customPassword.set(password);
    this.identity.select({ kind: 'custom', username, password });
  }

  send(): void {
    const operation = this.operation();
    const { headers, invalid } = parseHeaders(this.headersText());
    if (invalid.length > 0) {
      this.error.set(`Not a "Name: value" header line: ${invalid[0]}`);
      return;
    }

    let body = this.body();
    if (operation?.hasCommandId && body.trim()) {
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
    const identity = this.identity.label();

    this.error.set(null);
    this.pending.set(true);
    this.sending?.unsubscribe();
    this.sending = this.host.proxy(request).subscribe({
      next: (result) => {
        this.result.set(result);
        this.history.update((all) => [{ seq: ++this.seq, at: new Date(), name, identity, request, result }, ...all].slice(0, MAX_HISTORY));
        this.pending.set(false);
      },
      error: (e: unknown) => {
        this.error.set(this.describe(e));
        this.pending.set(false);
      },
    });
  }

  restore(entry: HistoryEntry): void {
    this.result.set(entry.result);
    this.method.set(entry.request.method);
    this.url.set(entry.request.url);
    this.pathValues.set({});
    this.queryValues.set({});
    this.body.set(entry.request.body ?? '');
    this.correlationId.set(entry.request.correlationId ?? '');
  }

  showToken(): void {
    const request = this.identity.request();
    this.token.set(null);
    if (!request) {
      this.error.set('Anonymous has no token.');
      return;
    }
    this.host.token(request).subscribe({
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

  /** A problem's `detail`/`title`, Keycloak's `error_description`, else the error's message. */
  private describe(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string; error_description?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.error_description ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
