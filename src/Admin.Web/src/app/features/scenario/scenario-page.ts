import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
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
  TokenClockView,
} from '../../core/host/host-types';
import { IdentityState } from '../../core/identity/identity-state';
import { DrainedIndicator } from '../../shared/drained-indicator/drained-indicator';
import { DRAIN_POLL_MS, DRAIN_WATCH_MS, UUID } from '../api/api-page';
import { ScenarioLauncher } from './scenario-launcher';
import { buildUrl, pretty, withFreshCommandId } from '../api/request-builder';
import {
  SCRIPTS,
  SCRIPT_KEYS,
  STEP_TITLES,
  ScenarioOperations,
  ScriptKey,
  StepKey,
  WATCHING_FOR,
  WatchKey,
  idFrom,
  orderRead,
  resolveOperations,
  secondsBetween,
  stepCorrelationId,
  verdict,
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
  /** Said when the host minted a new token for this step because the one it held was near or past expiry. */
  regrant: string | null;
  body: string | null;
}

type Responded = Extract<ProxyResult, { outcome: 'responded' }>;

/** Each state's word, because state is never carried by colour alone. */
const STATE_WORDS: Record<StepState, string> = {
  pending: '[pending]',
  running: '[running]',
  ok: '[ok]',
  failed: '[failed]',
  skipped: '[not run]',
};

function initialSteps(script: ScriptKey): StepView[] {
  return SCRIPTS[script].steps.map((key) => ({
    key,
    title: STEP_TITLES[key],
    state: 'pending',
    correlationId: null,
    request: null,
    status: null,
    detail: '',
    regrant: null,
    body: null,
  }));
}

/**
 * run-locally.md's "Call the APIs" as a run (spec §12, phase 6), in one of two scripts: an order
 * watched to delivered, or an order cancelled and watched to cancelled. The platform calls go
 * through `POST /api/proxy` as the API screen's calls do, each step with its own correlation id so
 * each has its own trace; the drain reads `GET /api/broker/queues` and has none. A watch reads the
 * BFF's order read with the drain's interval and cap. The first step that does not succeed ends the
 * run: every later one needs what it would have produced.
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
  /**
   * Ends the drain's and the watches' polls when the screen goes, and no step after them is sent. A
   * send already out is left to finish, and a placed order the cancel script was about to cancel is
   * still cancelled, so leaving never strands an order that script meant to undo.
   */
  private readonly stopped = new Subject<void>();
  private destroyed = false;
  /** Whether this run has sent as its user yet: a token missing after that has expired, not never been minted. */
  private sentThisRun = false;
  /**
   * Whether the BFF has answered for this run's order yet, across its watches: once it has, a 404 is the BFF
   * losing an order it held, not one it has still to learn of, and ends the step.
   */
  private orderAnswered = false;

  readonly identity = inject(IdentityState);
  private readonly launcher = inject(ScenarioLauncher);
  readonly scripts = SCRIPT_KEYS.map((key) => ({ key, title: SCRIPTS[key].title }));
  readonly script = signal<ScriptKey>('deliver');
  readonly intro = computed(() => SCRIPTS[this.script()].intro);
  readonly catalog = signal<ApiCatalogView | null>(null);
  readonly error = signal<string | null>(null);
  readonly steps = signal<StepView[]>(initialSteps('deliver'));
  readonly running = signal(false);
  readonly runId = signal<string | null>(null);
  readonly projection = signal<ProjectionDrain | null>(null);
  /** The realm user picked here; empty until one is, when the API screen's user is the default. */
  readonly username = signal('');

  readonly resolved = computed(() => {
    const view = this.catalog();
    return view ? resolveOperations(view, SCRIPTS[this.script()]) : null;
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
      // A request this screen never acted on would otherwise start a run on the next visit, unasked.
      this.launcher.take();
    });
    this.identity.load();
    this.host.operations().subscribe({
      next: (view) => this.catalog.set(view),
      error: (e: unknown) => this.error.set(describe(e)),
    });
    // The palette's "Run" waits for the catalog and a user, as the button does; one asked for during a run is
    // dropped rather than queued behind it, so a second keystroke never orders twice.
    effect(() => {
      if (!this.launcher.pending()) return;
      const running = this.running();
      const canRun = this.canRun();
      untracked(() => {
        if (running) this.launcher.take();
        else if (canRun && this.launcher.take()) void this.run();
      });
    });
  }

  /** A script picked between runs shows its own steps; the last run's results go with the old one. */
  pick(script: ScriptKey): void {
    if (this.running() || script === this.script()) return;
    this.script.set(script);
    this.steps.set(initialSteps(script));
    this.runId.set(null);
    this.projection.set(null);
  }

  /**
   * The host caches the catalog, so a service started after it loaded is seen only on a reload; the
   * realm users are asked for again too, since a failed first load leaves nobody to run as.
   */
  reload(): void {
    this.identity.load();
    this.host.reloadOperations().subscribe({
      next: (view) => {
        this.error.set(null);
        this.catalog.set(view);
      },
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

    const script = this.script();
    const run = this.uuid().slice(0, 8);
    const identity: Identity = { username, password: null };
    this.runId.set(run);
    this.error.set(null);
    this.running.set(true);
    this.sentThisRun = false;
    this.orderAnswered = false;
    this.projection.set(null);
    this.steps.set(initialSteps(script));

    try {
      await this.runSteps(SCRIPTS[script].steps, operations, run, identity);
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

  /** The script's steps in order, each carrying forward what it produced; the first that fails ends it. */
  private async runSteps(
    steps: readonly StepKey[],
    operations: ScenarioOperations,
    run: string,
    identity: Identity,
  ): Promise<void> {
    let productId = '';
    let orderId = '';

    for (const key of steps) {
      switch (key) {
        case 'publish': {
          const body = withFreshCommandId(operations.publish!.exampleBody ?? '', this.uuid());
          const id = await this.send(key, operations.publish!, run, identity, body, {}, true);
          if (id === null) return;
          productId = id;
          break;
        }
        case 'drain':
          if (!(await this.drain())) return;
          break;
        case 'quote': {
          const body = withProductId(operations.quote!.exampleBody ?? '', productId);
          if ((await this.send(key, operations.quote!, run, identity, body)) === null) return;
          break;
        }
        case 'order': {
          const placed = withFreshCommandId(operations.order!.exampleBody ?? '', this.uuid());
          const body = withProductId(placed, productId);
          const id = await this.send(key, operations.order!, run, identity, body, {}, true);
          if (id === null) return;
          orderId = id;
          break;
        }
        case 'cancel': {
          const cancel = operations.cancel!;
          const path = { [cancel.pathParameters[0]?.name ?? 'id']: orderId };
          const body = cancel.exampleBody ?? '';
          if ((await this.send(key, cancel, run, identity, body, path)) === null) return;
          break;
        }
        default:
          if (!(await this.watch(key, operations.read!, run, identity, orderId))) return;
      }
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
    // Once the screen has gone only the cancel still goes out: it undoes an order already placed.
    if (this.destroyed && key !== 'cancel') return null;

    const correlationId = stepCorrelationId(run, key);
    const url = buildUrl(operation.url, path, {});
    this.patch(key, { state: 'running', correlationId, request: `${operation.method} ${url}` });

    const result = await this.exchange(key, operation.method, url, body, identity, correlationId);
    if (result === null) return null;

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
   * Reads the order through the BFF until it shows the step `key` waits for, with the drain's
   * interval and cap, saying on every read what it is waiting for and what it last saw. A 404 before
   * the first answer is the BFF not having projected the order yet, and is waited out; any other
   * status ends the step as the platform answered it, a 401 included, and is never retried.
   */
  private async watch(
    key: WatchKey,
    read: ApiOperation,
    run: string,
    identity: Identity,
    orderId: string,
  ): Promise<boolean> {
    if (this.destroyed) return false;

    const correlationId = stepCorrelationId(run, key);
    const url = buildUrl(read.url, { [read.pathParameters[0]?.name ?? 'id']: orderId }, {});
    const waiting = `Waiting for ${WATCHING_FOR[key]}: reading the order every ${DRAIN_POLL_MS / 1000} s for up to ${DRAIN_WATCH_MS / 1000} s.`;
    this.patch(key, { state: 'running', correlationId, request: `${read.method} ${url}`, detail: waiting });

    const deadline = Date.now() + DRAIN_WATCH_MS;
    let last: string;
    let sentUnderId = false;

    for (;;) {
      const result = await this.exchange(key, read.method, url, '', identity, correlationId, sentUnderId);
      if (result === null) return false;
      sentUnderId = true;

      if (result.status === 404 && !this.orderAnswered) {
        last = 'The BFF answered 404: it has not learned of the order yet.';
      } else if (result.status !== 200) {
        this.patch(key, {
          state: 'failed',
          status: result.status,
          body: result.body,
          detail: `Answered ${result.status}.`,
        });
        return false;
      } else {
        this.orderAnswered = true;
        const order = orderRead(result.body);
        if (order === null) {
          this.patch(key, {
            state: 'failed',
            status: result.status,
            body: result.body,
            detail: 'Answered 200, but not with an order read the watch can follow.',
          });
          return false;
        }

        const v = verdict(key, order);
        if (v.kind === 'reached') {
          const after = secondsBetween(order.timeline.placed, v.at);
          this.patch(key, {
            state: 'ok',
            status: result.status,
            body: result.body,
            detail:
              after === null
                ? `The order read shows ${key} at ${v.at}.`
                : `The order read shows ${key} at ${v.at}, ${after} s after it was placed.`,
          });
          return true;
        }
        if (v.kind === 'failed') {
          this.patch(key, { state: 'failed', status: result.status, body: result.body, detail: v.reason });
          return false;
        }
        last = `Last read: ${order.status}, as the BFF knew it at ${order.asOf}.`;
        this.patch(key, { status: result.status, body: result.body });
      }

      if (Date.now() >= deadline) {
        this.patch(key, {
          state: 'failed',
          detail: `Not ${key} after ${DRAIN_WATCH_MS / 1000} s. ${last}`,
        });
        return false;
      }
      this.patch(key, { detail: `${waiting} ${last}` });

      await firstValueFrom(timer(DRAIN_POLL_MS).pipe(takeUntil(this.stopped)), {
        defaultValue: undefined,
      });
      if (this.destroyed) return false;
    }
  }

  /**
   * One request through the proxy, after reading the token clock. Resolves to the platform's answer,
   * or to null once a failure that left no answer has been recorded on the step. `sentBefore` is a
   * watch that has already read under this correlation id, whose trace a later unsent read does not
   * take away.
   */
  private async exchange(
    key: StepKey,
    method: string,
    url: string,
    body: string,
    identity: Identity,
    correlationId: string,
    sentBefore = false,
  ): Promise<Responded | null> {
    // Said only once the call has gone out: a grant Keycloak refused or never answered minted nothing.
    const regrant = await this.regrant(identity);
    const traced = sentBefore ? correlationId : null;

    let result: ProxyResult;
    try {
      result = await firstValueFrom(
        this.host.proxy({
          method,
          url,
          headers: {},
          body: body.trim() ? body : null,
          identity,
          correlationId,
        }),
      );
    } catch (e: unknown) {
      this.patch(key, { state: 'failed', correlationId: traced, detail: describe(e) });
      return null;
    }

    switch (result.outcome) {
      case 'tokenRejected':
        // This call never left the console, so only reads already sent under the id are traceable.
        this.patch(key, {
          state: 'failed',
          correlationId: traced,
          status: result.status,
          body: result.body,
          detail: sentBefore
            ? `Keycloak refused ${identity.username}: ${result.status}. This read was not sent.`
            : `Keycloak refused ${identity.username}: ${result.status}. Nothing was sent.`,
        });
        return null;
      case 'unreached':
        this.patch(key, {
          state: 'failed',
          correlationId: result.sent ? result.correlationId : traced,
          detail: `No answer from the platform: ${result.error}`,
          ...(result.sent && regrant ? { regrant } : {}),
        });
        return null;
    }

    this.sentThisRun = true;
    if (regrant) this.patch(key, { regrant });
    return result;
  }

  /**
   * What the host is about to do with the token, read from its clock before a call: past the moment
   * it stops reusing one (`TokenService.RenewsAt`, the one "near expiry" there is), or with none held
   * after this run has sent, the call mints a new one, and the step says so. Null when the held token
   * is reused, and when the clock does not answer, since the call then proceeds as it would have.
   */
  private async regrant(identity: Identity): Promise<string | null> {
    let clock: TokenClockView;
    try {
      clock = await firstValueFrom(this.host.tokenClock(identity));
    } catch {
      return null;
    }
    if (clock.held && clock.renewsAt && Date.now() >= Date.parse(clock.renewsAt)) {
      return `The token held for ${identity.username} was due to renew at ${clock.renewsAt}, so the host minted a new one for this call.`;
    }
    if (!clock.held && this.sentThisRun) {
      return `The token for ${identity.username} had expired, so the host minted a new one for this call.`;
    }
    return null;
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
    // `stopped` does not replay, so a screen that went while the publish was out is checked here.
    if (this.destroyed) return false;
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
