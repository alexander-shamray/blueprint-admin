import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  Subject,
  exhaustMap,
  firstValueFrom,
  lastValueFrom,
  takeUntil,
  takeWhile,
  tap,
  timer,
} from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import {
  ApiCatalogView,
  ApiOperation,
  Identity,
  ProjectionDrain,
  ProxyResult,
  QueuesView,
} from '../../core/host/host-types';
import { IdentityState } from '../../core/identity/identity-state';
import { DrainedIndicator } from '../../shared/drained-indicator/drained-indicator';
import { DRAIN_POLL_MS, DRAIN_WATCH_MS, UUID } from '../api/api-page';
import { buildUrl, pretty, withFreshCommandId } from '../api/request-builder';
import {
  STEP_KEYS,
  STEP_TITLES,
  StepKey,
  idFrom,
  resolveOperations,
  stepCorrelationId,
  withProductId,
} from './scenario-run';

export type StepState = 'pending' | 'running' | 'ok' | 'failed' | 'skipped';

export interface StepView {
  key: StepKey;
  title: string;
  state: StepState;
  /** Null until the step sends, and for the drain step, which asks the broker rather than the platform's HTTP surface. */
  correlationId: string | null;
  /** Method and URL as sent. */
  request: string | null;
  status: number | null;
  detail: string;
  body: string | null;
}

/** Each state's word, because state is never carried by colour alone. */
const STATE_WORDS: Record<StepState, string> = {
  pending: '[pending]',
  running: '[running]',
  ok: '[ok]',
  failed: '[failed]',
  skipped: '[not run]',
};

function initialSteps(): StepView[] {
  return STEP_KEYS.map((key) => ({
    key,
    title: STEP_TITLES[key],
    state: 'pending',
    correlationId: null,
    request: null,
    status: null,
    detail: '',
    body: null,
  }));
}

/**
 * run-locally.md's "Call the APIs" as one run (spec §12, phase 6): publish a product, wait for
 * ordering-catalog-events to drain, quote a basket holding it, order it, cancel the order. Every
 * call goes through `POST /api/proxy` as the API screen's do, each with its own correlation id so
 * each step has its own trace. The first step that does not succeed ends the run: every later one
 * needs what it would have produced.
 */
@Component({
  selector: 'app-scenario-page',
  imports: [FormsModule, RouterLink, DrainedIndicator],
  templateUrl: './scenario-page.html',
  styleUrl: './scenario-page.css',
  preserveWhitespaces: true,
})
export class ScenarioPage {
  private readonly host = inject(HostClient);
  private readonly uuid = inject(UUID);
  /** Ends a poll or a send still out when the screen goes; the run then stops at its next step. */
  private readonly stopped = new Subject<void>();
  private destroyed = false;

  readonly identity = inject(IdentityState);
  readonly catalog = signal<ApiCatalogView | null>(null);
  readonly error = signal<string | null>(null);
  readonly steps = signal<StepView[]>(initialSteps());
  readonly running = signal(false);
  readonly runId = signal<string | null>(null);
  readonly projection = signal<ProjectionDrain | null>(null);
  /** The realm user picked here; empty until one is, when the API screen's user is the default. */
  readonly username = signal('');

  readonly resolved = computed(() => {
    const view = this.catalog();
    return view ? resolveOperations(view) : null;
  });

  /** Who the run sends as: a realm user, since publishing, ordering and cancelling each need a permission. */
  readonly runAs = computed(() => {
    if (this.username()) return this.username();
    const c = this.identity.choice();
    return c.kind === 'user' ? c.username : (this.identity.users()[0]?.username ?? '');
  });

  readonly canRun = computed(
    () => !this.running() && this.resolved()?.operations != null && this.runAs() !== '',
  );

  /** One line for the polite live region, so a screen reader hears each step start and the run end. */
  readonly announcement = computed(() => {
    const steps = this.steps();
    const running = steps.find((s) => s.state === 'running');
    if (running) return `${running.title}: running`;
    const failed = steps.find((s) => s.state === 'failed');
    if (failed) return `Run stopped: ${failed.title} failed. ${failed.detail}`;
    return steps.every((s) => s.state === 'ok') ? 'Run complete: every step succeeded.' : '';
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.stopped.next();
    });
    this.identity.load();
    this.host.operations().subscribe({
      next: (view) => this.catalog.set(view),
      error: (e: unknown) => this.error.set(describe(e)),
    });
  }

  stateWord(state: StepState): string {
    return STATE_WORDS[state];
  }

  prettyBody(body: string): string {
    return pretty(body);
  }

  async run(): Promise<void> {
    const operations = this.resolved()?.operations;
    const username = this.runAs();
    if (!operations || !username || this.running()) return;

    const run = this.uuid().slice(0, 8);
    const identity: Identity = { username, password: null };
    this.runId.set(run);
    this.running.set(true);
    this.projection.set(null);
    this.steps.set(initialSteps());

    try {
      const publish = withFreshCommandId(operations.publish.exampleBody ?? '', this.uuid());
      const productId = await this.send(
        'publish',
        operations.publish,
        run,
        identity,
        publish,
        {},
        true,
      );
      if (productId === null || !(await this.drain())) return;

      const quote = withProductId(operations.quote.exampleBody ?? '', productId);
      if ((await this.send('quote', operations.quote, run, identity, quote)) === null) return;

      const placed = withFreshCommandId(operations.order.exampleBody ?? '', this.uuid());
      const order = withProductId(placed, productId);
      const orderId = await this.send('order', operations.order, run, identity, order, {}, true);
      if (orderId === null) return;

      const cancel = operations.cancel;
      const path = { [cancel.pathParameters[0]?.name ?? 'id']: orderId };
      await this.send('cancel', cancel, run, identity, cancel.exampleBody ?? '', path);
    } finally {
      this.steps.update((all) =>
        all.map((s) =>
          s.state === 'pending'
            ? { ...s, state: 'skipped', detail: 'An earlier step did not succeed.' }
            : s,
        ),
      );
      this.running.set(false);
    }
  }

  /**
   * Sends one step through the proxy and records what came back. Resolves to the id the answer
   * carries when `readId`, to `''` for any other success, and to null when the run must stop.
   */
  private async send(
    key: StepKey,
    operation: ApiOperation,
    run: string,
    identity: Identity,
    body: string,
    path: Record<string, string> = {},
    readId = false,
  ): Promise<string | null> {
    const correlationId = stepCorrelationId(run, key);
    const url = buildUrl(operation.url, path, {});
    this.patch(key, { state: 'running', correlationId, request: `${operation.method} ${url}` });

    let result: ProxyResult;
    try {
      result = await firstValueFrom(
        this.host
          .proxy({
            method: operation.method,
            url,
            headers: {},
            body: body.trim() ? body : null,
            identity,
            correlationId,
          })
          .pipe(takeUntil(this.stopped)),
      );
    } catch (e: unknown) {
      if (!this.destroyed) {
        this.patch(key, { state: 'failed', correlationId: null, detail: describe(e) });
      }
      return null;
    }

    switch (result.outcome) {
      case 'tokenRejected':
        // Nothing left the console, so there is nothing to trace.
        this.patch(key, {
          state: 'failed',
          correlationId: null,
          status: result.status,
          body: result.body,
          detail: `Keycloak refused ${identity.username}: ${result.status}. Nothing was sent.`,
        });
        return null;
      case 'unreached':
        this.patch(key, {
          state: 'failed',
          correlationId: result.sent ? result.correlationId : null,
          detail: `No answer from the platform: ${result.error}`,
        });
        return null;
    }

    const ok = result.status >= 200 && result.status < 300;
    const id = ok && readId ? idFrom(result.body) : null;
    if (!ok || (readId && id === null)) {
      const detail = ok
        ? `Answered ${result.status}, but not with the id the next steps need.`
        : `Answered ${result.status}.`;
      this.patch(key, { state: 'failed', status: result.status, body: result.body, detail });
      return null;
    }

    this.patch(key, {
      state: 'ok',
      status: result.status,
      body: result.body,
      detail: id ? `Answered ${result.status} with id ${id}.` : `Answered ${result.status}.`,
    });
    return id ?? '';
  }

  /**
   * run-locally.md: Ordering prices from its projection, so an order for a product just published
   * waits until ordering-catalog-events drains. Polled as the API screen's drain watch is, with its
   * interval and cap; anything short of drained ends the run rather than ordering against a
   * projection that may not hold the price.
   *
   * Drained is necessary, not sufficient: the publish stages an outbox row that the backend's
   * OutboxDispatcher sends later, so an empty queue can precede the event it is waited for. Nothing
   * the platform exposes says one product has been projected (the BFF quote prices from Catalog, not
   * from Ordering), so the wait is run-locally.md's, and a place-order that still finds no price
   * answers as the platform does. A product-specific signal is a blueprint-backend change.
   */
  private async drain(): Promise<boolean> {
    this.patch('drain', { state: 'running', detail: 'Polling the broker.' });

    let last: QueuesView | undefined;
    try {
      last = await lastValueFrom(
        timer(0, DRAIN_POLL_MS).pipe(
          exhaustMap(() => this.host.brokerQueues()),
          tap((view) => this.projection.set(view.reachable ? view.projection : null)),
          takeWhile(
            (view) => view.reachable && view.projection.found && !view.projection.drained,
            true,
          ),
          takeUntil(timer(DRAIN_WATCH_MS)),
          takeUntil(this.stopped),
        ),
        { defaultValue: undefined },
      );
    } catch (e: unknown) {
      // The last snapshot would otherwise sit beside the failure as though it were current.
      this.projection.set(null);
      if (!this.destroyed) this.patch('drain', { state: 'failed', detail: describe(e) });
      return false;
    }

    if (this.destroyed) return false;
    if (!last) {
      this.patch('drain', {
        state: 'failed',
        detail: 'The broker did not answer before the wait ran out.',
      });
      return false;
    }
    if (!last.reachable) {
      this.patch('drain', {
        state: 'failed',
        detail: `The broker did not answer: ${last.error ?? 'no reason given'}.`,
      });
      return false;
    }

    const p = last.projection;
    if (!p.found) {
      this.patch('drain', {
        state: 'failed',
        detail: `${p.queue} is not declared: Ordering has not started, so it cannot price an order.`,
      });
      return false;
    }
    if (!p.drained) {
      this.patch('drain', {
        state: 'failed',
        detail: `${p.queue} still holds ${p.messages ?? 'some'} messages after ${DRAIN_WATCH_MS / 1000} s; the Broker screen shows where they are.`,
      });
      return false;
    }

    this.patch('drain', {
      state: 'ok',
      detail: `${p.queue} is empty, which is what run-locally.md waits for before ordering.`,
    });
    return true;
  }

  private patch(key: StepKey, change: Partial<StepView>): void {
    this.steps.update((all) => all.map((s) => (s.key === key ? { ...s, ...change } : s)));
  }
}

/** A problem's `detail`/`title`, else the error's message. */
function describe(e: unknown): string {
  const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
  return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
}
