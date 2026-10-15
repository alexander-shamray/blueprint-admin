import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { EMPTY, Observable, Subscription, catchError, finalize, of, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ErasureRequestView, HoldersView, Identity, ProxyResult } from '../../core/host/host-types';

/** A read each tick, as the Broker screen's refresh is: nothing the host keeps among its jobs. */
export const WATCH_MS = 3000;

/**
 * The realm login the gateway's `/api/v1/privacy/` route lets through: it holds `privacy:erase` and nothing else, and
 * `demo` does not (blueprint-backend `deploy/compose/README.md`). The screen sends as it whatever the API screen chose.
 */
export const PRIVACY_IDENTITY: Identity = { username: 'privacy', password: null };

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** One holder of the request: where it stands, what it answered, and how deep its queue is. */
export interface HolderRow {
  name: string;
  state: 'answered' | 'waiting' | 'overdue' | 'unasked';
  count: number | null;
  receivedAt: string | null;
  counted: boolean;
  queue: string | null;
  pending: number | null;
}

@Component({
  selector: 'app-erasure-page',
  imports: [FormsModule, RouterLink],
  templateUrl: './erasure-page.html',
  styleUrl: './erasure-page.css',
  preserveWhitespaces: true,
})
export class ErasurePage {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  private watching?: Subscription;

  private readonly config = toSignal(this.host.config().pipe(catchError(() => of(undefined))));

  readonly subject = signal('');
  readonly requestId = signal('');
  readonly request = signal<ErasureRequestView | null>(null);
  readonly holders = signal<HoldersView | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  readonly watch = signal(false);
  /** The correlation id of the raise, which Trace reads the whole fan-out under. */
  readonly raisedUnder = signal<string | null>(null);

  readonly canRaise = computed(() => GUID.test(this.subject().trim()) && !this.busy());
  /** Not gated on `busy`: a raise and a reissue read the request from inside their own call, while it is still out. */
  readonly canRead = computed(() => GUID.test(this.requestId().trim()));

  /** The request's holders, or the backend's list alone while no request is open. Answers outside the set follow, marked as such. */
  readonly rows = computed<HolderRow[]>(() => {
    const request = this.request();
    const listed = this.holders()?.holders ?? [];
    const depth = (name: string) => listed.find((h) => h.name === name);

    if (request === null) {
      return listed.map((h) => ({ name: h.name, state: 'unasked', count: null, receivedAt: null, counted: true, queue: h.queue, pending: h.pending }));
    }

    const expected = request.responders.map((name): HolderRow => {
      const answer = request.answers.find((a) => a.responder === name);
      return {
        name,
        state: answer ? 'answered' : request.status === 'Overdue' ? 'overdue' : 'waiting',
        count: answer?.count ?? null,
        receivedAt: answer?.receivedAt ?? null,
        counted: true,
        queue: depth(name)?.queue ?? null,
        pending: depth(name)?.pending ?? null,
      };
    });
    const extra = request.answers
      .filter((a) => !request.responders.includes(a.responder))
      .map((a): HolderRow => ({ name: a.responder, state: 'answered', count: a.count, receivedAt: a.receivedAt, counted: a.counted, queue: null, pending: null }));

    return [...expected, ...extra];
  });

  readonly overdue = computed(() => this.request()?.status === 'Overdue');

  constructor() {
    this.destroyRef.onDestroy(() => this.watching?.unsubscribe());
    this.host
      .erasureHolders()
      .pipe(takeUntilDestroyed(this.destroyRef), catchError(() => of(null)))
      .subscribe((view) => this.holders.set(view));
  }

  newSubject(): void {
    this.subject.set(crypto.randomUUID());
  }

  /** `POST /api/v1/privacy/erasure-requests`: asking again for a subject with a request open answers that request. */
  raise(): void {
    if (!this.canRaise()) return;
    this.send('POST', '/api/v1/privacy/erasure-requests', JSON.stringify({ subjectId: this.subject().trim() })).subscribe((result) => {
      const body = this.expect(result, 'Raising the request');
      if (body === null) return;
      try {
        const id = JSON.parse(body) as unknown;
        if (typeof id !== 'string') throw new Error('not an id');
        this.requestId.set(id);
        this.raisedUnder.set(result.correlationId);
        this.request.set(null);
        this.read();
      } catch {
        this.error.set(`Privacy accepted the request but answered no request id: ${body}`);
      }
    });
  }

  /** `GET .../{id}`: one holder row per responder the request was raised with. */
  read(): void {
    if (!this.canRead()) return;
    this.send('GET', `/api/v1/privacy/erasure-requests/${this.requestId().trim()}`).subscribe((result) => {
      const body = this.expect(result, 'Reading the request');
      if (body === null) return;
      try {
        const view = JSON.parse(body) as ErasureRequestView;
        this.request.set(view);
        if (view.status === 'Closed') this.setWatch(false);
        this.refreshDepths();
      } catch {
        this.error.set(`Privacy answered something that is not an erasure request: ${body}`);
      }
    });
  }

  /** `POST .../{id}/reissue`: asks the holders again under the same request, then reads it. Keyed, since a repeat would broadcast again. */
  reissue(): void {
    const request = this.request();
    if (!request || !this.overdue() || this.busy()) return;
    this.send('POST', `/api/v1/privacy/erasure-requests/${request.requestId}/reissue`, JSON.stringify({ commandId: crypto.randomUUID() })).subscribe((result) => {
      if (this.expect(result, 'Reissuing the request') !== null) this.read();
    });
  }

  setWatch(on: boolean): void {
    this.watch.set(on);
    this.watching?.unsubscribe();
    this.watching = on ? timer(WATCH_MS, WATCH_MS).subscribe(() => this.read()) : undefined;
  }

  private refreshDepths(): void {
    this.host
      .erasureHolders()
      .pipe(takeUntilDestroyed(this.destroyRef), catchError(() => of(null)))
      .subscribe((view) => view && this.holders.set(view));
  }

  private send(method: string, path: string, body: string | null = null): Observable<ProxyResult> {
    const gateway = this.config()?.urls.gateway ?? '';
    this.busy.set(true);
    this.error.set(null);
    return this.host
      .proxy({ method, url: gateway.replace(/\/+$/, '') + path, headers: body ? { 'Content-Type': 'application/json' } : {}, body, identity: PRIVACY_IDENTITY, correlationId: null })
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        catchError((e: unknown) => {
          this.error.set(this.describe(e));
          return EMPTY;
        }),
        finalize(() => this.busy.set(false)),
      );
  }

  /** The body of a 2xx answer, or null after saying why the call did not succeed, in the platform's own words. */
  private expect(result: ProxyResult, what: string): string | null {
    if (result.outcome === 'unreached') {
      this.error.set(`${what}: the request never got an answer (${result.error}).`);
      return null;
    }
    if (result.outcome === 'tokenRejected') {
      this.error.set(`${what}: Keycloak refused the privacy login (${result.status}). A realm imported before it existed needs docker compose down -v.`);
      return null;
    }
    if (result.status < 200 || result.status >= 300) {
      this.error.set(`${what}: the gateway answered ${result.status}${result.body ? ` — ${result.body}` : ''}`);
      return null;
    }
    return result.body;
  }

  private describe(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
