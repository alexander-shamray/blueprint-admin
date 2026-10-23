import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, catchError, finalize, map, of, tap } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ErasureRequestView, HoldersView, Identity, ProxyResult } from '../../core/host/host-types';
import { PolledFeed, TabVisibility } from '../../workspace/polled-feed';

/** A read each tick, as the Broker screen's refresh is: nothing the host keeps among its jobs. */
export const WATCH_MS = 3000;

/**
 * The realm login the gateway's `/api/v1/privacy/` route lets through: it holds `privacy:erase` and nothing else, and
 * `demo` does not (blueprint-backend `deploy/compose/README.md`). The widget sends as it whatever the API screen chose.
 */
export const PRIVACY_IDENTITY: Identity = { username: 'privacy', password: null };

export const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

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

/**
 * The erasure request being watched (widget-workspace spec §4.2, §5.2): which request, what it last read, the holders'
 * queue depths and whether it is being read every 3 seconds. The Erasure widget is a view of it, so leaving the page
 * and coming back finds the same request; the subject being typed is the widget's session state, not kept here. The
 * watch is a `PolledFeed`, so it reads only while a widget that switched it on is showing and the tab is visible (§4.3).
 */
@Injectable({ providedIn: 'root' })
export class ErasureRun {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);

  private readonly config = toSignal(this.host.config().pipe(catchError(() => of(undefined))));

  readonly requestId = signal('');
  readonly request = signal<ErasureRequestView | null>(null);
  readonly holders = signal<HoldersView | null>(null);
  readonly error = signal<string | null>(null);
  /** Calls out, counted: a raise or reissue reads from inside its own call, so a flag would clear while that read is still out. */
  private readonly inFlight = signal(0);
  readonly busy = computed(() => this.inFlight() > 0);
  /** The error showing was set by a watch tick, which the next good tick clears; the operator's own errors stay. */
  private quietFailure = false;
  readonly watch = signal(false);
  /** The correlation id of the raise, which Trace reads the whole fan-out under. */
  readonly raisedUnder = signal<string | null>(null);

  /** Not gated on `busy`: a raise and a reissue read the request from inside their own call, while it is still out. */
  readonly canRead = computed(() => GUID.test(this.requestId().trim()));

  /** Reads the request every `WATCH_MS` while a widget that switched the watch on is showing. */
  readonly watchFeed = new PolledFeed<void>(
    // Quiet, and not while a call of the operator's is out: a tick must not disable Raise and Reissue, clear an
    // error being read, or read the request a second time beside the read its caller is about to make.
    () => (this.canRead() && !this.busy() ? this.fetchRequest(true) : EMPTY),
    WATCH_MS,
    inject(TabVisibility).visible$,
    this.destroyRef,
  );

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
    this.host
      .erasureHolders()
      .pipe(takeUntilDestroyed(this.destroyRef), catchError(() => of(null)))
      .subscribe((view) => this.holders.set(view));
  }

  /** `POST /api/v1/privacy/erasure-requests`: asking again for a subject with a request open answers that request. */
  raise(subject: string): void {
    if (!GUID.test(subject.trim()) || this.busy()) return;
    this.send('POST', '/api/v1/privacy/erasure-requests', JSON.stringify({ subjectId: subject.trim() })).subscribe((result) => {
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
    this.fetchRequest().subscribe();
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
  }

  /** `quiet` is the watch's read: it is not a call of the operator's, so it neither counts as busy nor clears an error. */
  private fetchRequest(quiet = false): Observable<void> {
    return this.send('GET', `/api/v1/privacy/erasure-requests/${this.requestId().trim()}`, null, quiet).pipe(
      tap((result) => {
        const body = this.expect(result, 'Reading the request');
        if (body === null) {
          if (quiet) this.quietFailure = true;
          return;
        }
        // A tick that reads well takes away the failure an earlier tick showed; an error of the operator's own call
        // is not the tick's to clear.
        if (quiet && this.quietFailure) {
          this.quietFailure = false;
          this.error.set(null);
        }
        try {
          const view = JSON.parse(body) as ErasureRequestView;
          this.request.set(view);
          if (view.status === 'Closed') this.setWatch(false);
          this.refreshDepths();
        } catch {
          this.error.set(`Privacy answered something that is not an erasure request: ${body}`);
        }
      }),
      map((): void => undefined),
    );
  }

  private refreshDepths(): void {
    this.host
      .erasureHolders()
      .pipe(takeUntilDestroyed(this.destroyRef), catchError(() => of(null)))
      .subscribe((view) => view && this.holders.set(view));
  }

  private send(method: string, path: string, body: string | null = null, quiet = false): Observable<ProxyResult> {
    const gateway = this.config()?.urls.gateway ?? '';
    if (!quiet) {
      this.inFlight.update((n) => n + 1);
      this.error.set(null);
      this.quietFailure = false;
    }
    return this.host
      .proxy({ method, url: gateway.replace(/\/+$/, '') + path, headers: body ? { 'Content-Type': 'application/json' } : {}, body, identity: PRIVACY_IDENTITY, correlationId: null })
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        catchError((e: unknown) => {
          if (quiet) this.quietFailure = true;
          this.error.set(this.describe(e));
          return EMPTY;
        }),
        finalize(() => {
          if (!quiet) this.inFlight.update((n) => n - 1);
        }),
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
