import { Injectable, computed, inject, signal } from '@angular/core';
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
  LeaseChange,
  MailListView,
  ProjectionDrain,
  ProxyResult,
  QueuesView,
  ReviewFlagView,
  TokenClockView,
} from '../../core/host/host-types';
import { IdentityState } from '../../core/identity/identity-state';
import { RunningWork } from '../../workspace/running-work';
import { DRAIN_POLL_MS, DRAIN_WATCH_MS, UUID } from '../api/api-client';
import { TraceRead, buildExport } from './scenario-export';
import { buildUrl, withFreshCommandId } from '../api/request-builder';
import {
  CARRIER_POLL_MS,
  CARRIER_WATCH_MS,
  RESERVATION_POLL_MS,
  RESERVATION_WATCH_MS,
  REVIEW_POLL_MS,
  REVIEW_WATCH_MS,
  CarrierEntry,
  SagaRead,
  SagaWord,
  SCRIPTS,
  SCRIPT_KEYS,
  STEP_TITLES,
  ScenarioOperations,
  Script,
  ScriptKey,
  StepKey,
  WATCHING_FOR,
  WatchKey,
  carrierEntries,
  carrierVerdict,
  idFrom,
  imageFault,
  orderRead,
  reservationVerdict,
  resolveOperations,
  sagaStrip,
  sagaWord,
  reviewVerdict,
  secondsBetween,
  stepCorrelationId,
  thumbnailFrom,
  uploadFrom,
  verdict,
  hasField,
  hasShippingPostalCode,
  mailVerdict,
  isAlreadyShipped,
  withCarrierCode,
  withFeedOrder,
  withField,
  withProductId,
  withShippingPostalCode,
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

/** The reads the strip keeps: a watch polls every few seconds for minutes, and the first and last are what it shows. */
const SAGA_READS_KEPT = 200;

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
 * run-locally.md's "Call the APIs" as a run (spec §12, phase 6), in one of the scripts `SCRIPTS`
 * holds: an order watched to delivered, to cancelled, or to the saga's refusal of it for a cause
 * run-locally.md names (a declined amount, no stock). The platform calls go
 * through `POST /api/proxy` as the API screen's calls do, each step with its own correlation id so
 * each has its own trace; the drain reads `GET /api/broker/queues` and has none. A watch reads the
 * BFF's order read with the drain's interval and cap. The first step that does not succeed ends the
 * run: every later one needs what it would have produced.
 *
 * The run is running work (widget-workspace spec §4.2, §4.4): it lives here, at the root, and not in a screen, so
 * leaving the page that shows it neither stops nor loses it. Besides a step that fails, only Cancel, a host that stops
 * answering, or a page reload ends a run. Every Scenario and Saga strip widget is a view of this one run.
 */
@Injectable({ providedIn: 'root' })
export class ScenarioRunner {
  private readonly host = inject(HostClient);
  private readonly uuid = inject(UUID);
  /**
   * Ends the drain's and the watches' polls on Cancel, and no step after them is sent. A send already out is left to
   * finish, and a placed order the cancel script was about to cancel is still cancelled, so a Cancel never strands an
   * order that script meant to undo.
   */
  private readonly stopped = new Subject<void>();

  /**
   * True once Cancel was pressed and nothing more is to be sent. It ends the polls and stops the steps; it does not stop
   * the `restore` step, which undoes a change to the stack and so runs whatever happened.
   */
  private cancelled = false;

  /** Set while Cancel is going, so a second press does nothing and the button says so. */
  readonly cancelling = signal(false);

  /** Whether the `lease` step took a lease this run has still to end. */
  private leaseTaken = false;

  /** When this run's order was placed, in ms, as the order read gave it: where the carrier step starts reading the simulator's log. */
  private placedAt = 0;

  /** The mark this run put in its order's address, so the carrier step reads this run's bookings and not the whole journal's. */
  private carrierMarker = '';

  /** The changes the host may make for a script, by id, for the note before Run: the host's table, not a copy of it. */
  readonly changes = signal<LeaseChange[]>([]);
  /** Whether this run has sent as its user yet: a token missing after that has expired, not never been minted. */
  private sentThisRun = false;
  /**
   * Whether the BFF has answered for this run's order yet, across its watches: once it has, a 404 is the BFF
   * losing an order it held, not one it has still to learn of, and ends the step.
   */
  private orderAnswered = false;

  readonly identity = inject(IdentityState);
  readonly scripts = SCRIPT_KEYS.map((key) => ({ key, title: SCRIPTS[key].title }));
  readonly script = signal<ScriptKey>('deliver');
  readonly intro = computed(() => SCRIPTS[this.script()].intro);
  readonly catalog = signal<ApiCatalogView | null>(null);
  readonly error = signal<string | null>(null);
  readonly steps = signal<StepView[]>(initialSteps('deliver'));
  readonly running = signal(false);
  readonly runId = signal<string | null>(null);
  readonly projection = signal<ProjectionDrain | null>(null);

  /** Every order read the run's watches made, oldest first and capped; the strip is computed from them (spec §5.13). */
  readonly sagaReads = signal<SagaRead[]>([]);
  readonly strip = computed(() => sagaStrip(this.sagaReads()));
  /** The broker's queues as the drain step first read them, kept for the export's depths. */
  private queuesBefore: QueuesView | null = null;
  readonly exporting = signal(false);
  readonly exportError = signal<string | null>(null);
  /** True once a run has ended, so there is a finished run to write up. */
  readonly finished = signal(false);
  private exportedScript: ScriptKey = 'deliver';
  private exportedUser = '';
  /** The transcript's highest sequence when the run began, so the export carries the run's lines and not the session's; null where it could not be read. */
  private transcriptAfter: number | null = null;
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

  /**
   * What Run will change in the stack and for how long, said before it is pressed (spec §5.3): the host's own titles for the
   * script's changes, and the hold the host restores itself at. Empty for a script that changes nothing.
   */
  readonly leaseNote = computed(() => {
    const lease = SCRIPTS[this.script()].lease;
    if (!lease) return '';
    const table = this.changes();
    const named = lease.changes.map((id) => table.find((c) => c.id === id)?.title ?? id);
    const services = [
      ...new Set(lease.changes.map((id) => table.find((c) => c.id === id)?.service ?? id)),
    ];
    return `Run changes the stack: ${named.join(', then ')} (${services.join(', ')}). Everything is restored when the run ends, is cancelled or fails, and the host restores it by itself after ${Math.round(lease.holdSeconds / 60)} minutes at most.`;
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
    // The shell's indicator says a run is going while no page shows it (§4.3): a run nobody can see is one they forget
    // while it holds a lease.
    inject(RunningWork).report('scenario', () => {
      if (!this.running()) return null;
      const steps = this.steps();
      const at = steps.findIndex((s) => s.state === 'running');
      return {
        text: at < 0 ? 'Scenario: running' : `Scenario: step ${at + 1}/${steps.length} running`,
        link: '/scenario',
      };
    });
  }

  /**
   * What a view needs before Run: the realm users, the operation catalog and the host's lease table, read again each
   * time a view opens, as the screen did.
   */
  load(): void {
    this.identity.load();
    this.host.operations().subscribe({
      next: (view) => this.catalog.set(view),
      error: (e: unknown) => this.error.set(describe(e)),
    });
    // The note before Run names what a script's lease will change; without the table it is left out, not guessed.
    this.host.lease().subscribe({
      next: (state) => this.changes.set(state.changes),
      error: () => this.changes.set([]),
    });
  }

  /**
   * The finished run as one Markdown file (spec §5.13): the page writes it from what it holds and reads the rest (the
   * transcript, each step's trace, the queues now) from the host, then the host's scrubber has the last word before the
   * download. Nothing is stored or posted anywhere.
   */
  async export(): Promise<void> {
    const run = this.runId();
    if (!run || this.running() || this.exporting()) return;
    this.exporting.set(true);
    this.exportError.set(null);
    try {
      const steps = this.steps();
      const ids = [...new Set(steps.flatMap((s) => (s.correlationId ? [s.correlationId] : [])))];
      const [transcript, queuesAfter, ...traces] = await Promise.all([
        firstValueFrom(this.host.transcriptScript(this.transcriptAfter ?? undefined)).catch(() => null),
        firstValueFrom(this.host.brokerQueues()).catch(() => null),
        ...ids.map((id) =>
          firstValueFrom(this.host.trace(id)).catch((e: unknown): TraceRead => describe(e)),
        ),
      ]);
      const read: Record<string, TraceRead> = {};
      ids.forEach((id, i) => (read[id] = traces[i]));
      const markdown = buildExport({
        title: SCRIPTS[this.exportedScript].title,
        run,
        username: this.exportedUser,
        exportedAt: new Date().toISOString(),
        steps,
        strip: this.strip(),
        transcript,
        transcriptScope: this.transcriptAfter === null ? 'session' : 'run',
        traces: read,
        queuesBefore: this.queuesBefore,
        queuesAfter,
      });
      const scrubbed = await firstValueFrom(this.host.scenarioExport(markdown));
      this.download(`scenario-${run}.md`, scrubbed);
    } catch (e: unknown) {
      this.exportError.set(describe(e));
    } finally {
      this.exporting.set(false);
    }
  }

  /** The highest sequence the transcript holds now; the entries after it are this run's. Null where the host would not say. */
  private async transcriptMark(): Promise<number | null> {
    try {
      const view = await firstValueFrom(this.host.transcript());
      return view.entries.reduce((high, e) => Math.max(high, e.sequence), 0);
    } catch {
      return null;
    }
  }

  private download(name: string, text: string): void {
    const url = URL.createObjectURL(new Blob([text], { type: 'text/markdown;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    URL.revokeObjectURL(url);
  }

  /** A script picked between runs shows its own steps; the last run's results go with the old one. */
  pick(script: ScriptKey): void {
    if (this.running() || script === this.script()) return;
    this.script.set(script);
    this.steps.set(initialSteps(script));
    this.runId.set(null);
    this.projection.set(null);
    this.sagaReads.set([]);
    this.finished.set(false);
    this.exportError.set(null);
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
    this.cancelled = false;
    this.cancelling.set(false);
    this.leaseTaken = false;
    this.placedAt = 0;
    this.carrierMarker = '';
    this.projection.set(null);
    this.sagaReads.set([]);
    this.queuesBefore = null;
    this.finished.set(false);
    this.exportError.set(null);
    this.exportedScript = script;
    this.exportedUser = username;
    this.transcriptAfter = await this.transcriptMark();
    this.steps.set(initialSteps(script));

    try {
      await this.runSteps(SCRIPTS[script], operations, run, identity);
    } finally {
      // Whatever ended the steps, a change to the stack is undone before the run is over: the `restore` step does not wait
      // for the steps before it to have succeeded, and Cancel does not stop it.
      await this.restoreLease(script);
      const why = this.cancelled ? 'The run was cancelled.' : 'An earlier step did not succeed.';
      this.steps.update((all) =>
        all.map((s) => {
          if (s.state === 'running') {
            return { ...s, state: 'failed', detail: this.cancelled ? 'Cancelled.' : s.detail };
          }
          return s.state === 'pending' ? { ...s, state: 'skipped', detail: why } : s;
        }),
      );
      this.running.set(false);
      this.cancelling.set(false);
      this.finished.set(true);
    }
  }

  /**
   * Ends the run from the console: the polls stop, no step after the one in flight is sent, and the `restore` step still
   * runs, so a cancelled run leaves the stack as it found it (spec §5.3).
   */
  cancel(): void {
    if (!this.running() || this.cancelling()) return;
    this.cancelling.set(true);
    this.cancelled = true;
    this.stopped.next();
  }

  /** The script's steps in order, each carrying forward what it produced; the first that fails ends it. */
  private async runSteps(
    script: Script,
    operations: ScenarioOperations,
    run: string,
    identity: Identity,
  ): Promise<void> {
    let productId = '';
    let orderId = '';
    let imageKey = '';

    // The `restore` step is not in this loop: it runs after it, whatever happened in it (see `run`).
    for (const key of script.steps.filter((k) => k !== 'restore')) {
      switch (key) {
        case 'lease':
          if (!(await this.takeLease(script))) return;
          break;
        case 'carrier':
          if (!(await this.carrierLog(script, operations, orderId))) return;
          break;
        case 'flagged':
          if (!(await this.reviewFlag(script, orderId))) return;
          break;
        case 'mail':
          if (!(await this.mailCount(script, orderId))) return;
          break;
        case 'reserved':
          if (!(await this.reservation(operations.reservation!, run, identity, orderId))) return;
          break;
        case 'stop':
          if (!(await this.takeLateChange(script))) return;
          break;
        case 'upload': {
          const uploaded = await this.upload(operations.image!, run, identity);
          if (uploaded === null) return;
          imageKey = uploaded;
          break;
        }
        case 'thumbnail':
          if (!(await this.thumbnail(operations.product!, run, identity, productId))) return;
          break;
        case 'publish': {
          const fresh = withFreshCommandId(operations.publish!.exampleBody ?? '', this.uuid());
          // An unhappy branch's cause that is a price is set here and nowhere else.
          const priced =
            script.amount === undefined ? fresh : withField(fresh, 'amount', script.amount);
          // The key the upload returned, never a URL (run-locally.md, "Attach a product image by hand").
          const body = imageKey ? withField(priced, 'imageKey', imageKey) : priced;
          const id = await this.send(key, operations.publish!, run, identity, body, {}, true);
          if (id === null) return;
          productId = id;
          break;
        }
        case 'drain':
          if (!(await this.drain())) return;
          break;
        case 'stock': {
          const stock = operations.stock!;
          const onHand = script.onHand ?? 0;
          const path = { [stock.pathParameters[0]?.name ?? 'productId']: productId };
          const body = withField(stock.exampleBody ?? '', 'onHand', onHand);
          if ((await this.send(key, stock, run, identity, body, path)) === null) return;
          this.patch(key, {
            detail: hasField(body, 'onHand', onHand)
              ? `Answered with the stock on hand set to ${onHand}.`
              : 'Answered, but the example body has no onHand to set, so the stock is unchanged.',
          });
          break;
        }
        case 'quote': {
          const body = withProductId(operations.quote!.exampleBody ?? '', productId);
          if ((await this.send(key, operations.quote!, run, identity, body)) === null) return;
          break;
        }
        case 'order': {
          const placed = withFreshCommandId(operations.order!.exampleBody ?? '', this.uuid());
          const stocked = withProductId(placed, productId);
          // The carrier's outcome is scripted by the delivery postal code, set here and nowhere else.
          if (script.carrier) this.carrierMarker = `scenario-${run}`;
          const body =
            script.postalCode === undefined
              ? stocked
              : withShippingPostalCode(stocked, script.postalCode, this.carrierMarker || undefined);
          const id = await this.send(key, operations.order!, run, identity, body, {}, true);
          if (id === null) return;
          if (script.postalCode !== undefined) {
            this.patch(key, {
              detail: hasShippingPostalCode(body, script.postalCode)
                ? `Answered with id ${id}, to the delivery postal code ${script.postalCode}.`
                : `Answered with id ${id}, but the example body has no shippingAddress to set, so the carrier takes its default.`,
            });
          }
          orderId = id;
          break;
        }
        case 'late_cancel': {
          if (!(await this.lateCancel(operations.cancel!, run, identity, orderId))) return;
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
          if (!(await this.watch(key, operations.read!, run, identity, orderId, operations.saga))) return;
      }
    }
  }

  /**
   * Reads what the carrier saw of this order from the simulator's request log (run-locally.md), filtered by the simulator
   * itself for the script's code and this run's mark, and the strange feed for this order's own path, until the log shows
   * the outcome the code scripts or the wait runs out. The simulator is
   * anonymous and untraced, so nothing is sent as the user and the step has no trace. A log cut short by the proxy's
   * cap, or an answer that is not a list of entries, fails the step: it never reads as a log with nothing in it.
   */
  private async carrierLog(script: Script, operations: ScenarioOperations, orderId: string): Promise<boolean> {
    const carrier = script.carrier;
    if (!carrier || this.cancelled) return false;

    const key: StepKey = 'carrier';
    const bookings = operations.bookings!;
    const polls = operations.polls;
    const bookingBody = withCarrierCode(bookings.exampleBody ?? '', carrier.code, this.carrierMarker || undefined);
    const since = this.placedAt;
    const waiting = `Waiting for the carrier simulator's log to show ${carrier.code}: reading it every ${CARRIER_POLL_MS / 1000} s for up to ${CARRIER_WATCH_MS / 1000} s, from when the order was placed.`;
    this.patch(key, { state: 'running', request: `${bookings.method} ${bookings.url}`, detail: waiting });

    const deadline = Date.now() + CARRIER_WATCH_MS;

    for (;;) {
      const found = await this.findInCarrierLog(key, bookings, bookingBody);
      if (found === null) return false;
      let feed: CarrierEntry[] = [];
      if (carrier.outcome === 'strange' && polls) {
        const read = await this.findInCarrierLog(key, polls, withFeedOrder(polls.exampleBody ?? '', orderId));
        if (read === null) return false;
        feed = read;
      }

      const v = carrierVerdict(carrier.outcome, found, feed, since);
      if (v.kind === 'reached') {
        this.patch(key, { state: 'ok', detail: v.detail });
        return true;
      }
      if (v.kind === 'failed') {
        this.patch(key, { state: 'failed', detail: v.reason });
        return false;
      }
      if (Date.now() >= deadline) {
        this.patch(key, { state: 'failed', detail: `Not shown after ${CARRIER_WATCH_MS / 1000} s. ${v.detail}` });
        return false;
      }
      this.patch(key, { detail: `${waiting} ${v.detail}` });

      await firstValueFrom(timer(CARRIER_POLL_MS).pipe(takeUntil(this.stopped)), { defaultValue: undefined });
      if (this.cancelled) return false;
    }
  }

  /**
   * Reads what Ordering logged when it flagged the order for review (run-locally.md, the Loki query), through the host,
   * until the line for the script's reason is there or the wait runs out. The host asks Loki by the order's own id, so the
   * lines are this order's and nothing is sent as the user and the step has no trace. A Loki that cannot be asked fails the
   * step: it never reads as an order nobody flagged.
   */
  private async reviewFlag(script: Script, orderId: string): Promise<boolean> {
    const review = script.review;
    if (!review || this.cancelled) return false;

    const key: StepKey = 'flagged';
    const waiting = `Waiting for Ordering to log the order as flagged for review as ${review.reason}: reading it every ${REVIEW_POLL_MS / 1000} s for up to ${REVIEW_WATCH_MS / 1000} s.`;
    this.patch(key, { state: 'running', request: `GET /api/review/${orderId}`, detail: waiting });

    const deadline = Date.now() + REVIEW_WATCH_MS;

    for (;;) {
      let view: ReviewFlagView;
      try {
        view = await firstValueFrom(this.host.reviewFlags(orderId));
      } catch (e: unknown) {
        if (this.cancelled) return false;
        this.patch(key, { state: 'failed', status: null, body: null, detail: describe(e) });
        return false;
      }
      // A Cancel while the read was in flight ends the step without another wait.
      if (this.cancelled) return false;

      const v = reviewVerdict(view, review.reason, this.placedAt);
      if (v.kind === 'reached') {
        this.patch(key, { state: 'ok', detail: v.detail });
        return true;
      }
      if (v.kind === 'failed') {
        this.patch(key, { state: 'failed', detail: v.reason });
        return false;
      }
      if (Date.now() >= deadline) {
        this.patch(key, { state: 'failed', detail: `Not logged after ${REVIEW_WATCH_MS / 1000} s. ${v.detail}` });
        return false;
      }
      this.patch(key, { detail: `${waiting} ${v.detail}` });

      await firstValueFrom(timer(REVIEW_POLL_MS).pipe(takeUntil(this.stopped)), { defaultValue: undefined });
      if (this.cancelled) return false;
    }
  }

  /**
   * Counts the mail the run sent, read from the mail sink through the host as the Mail screen reads it, until the sink holds
   * one message for each event the script expects or the wait runs out. The messages are the order's by their correlation id
   * (the order's own id, which Notifications stamps), so a second run's mail is never counted; the wait is the drain's interval and cap, since the send worker is asynchronous.
   * A second message for one event fails the step at once. The sink is anonymous and untraced, so nothing is sent as the user
   * and the step has no trace.
   */
  private async mailCount(script: Script, orderId: string): Promise<boolean> {
    const expected = script.mail;
    if (!expected || this.cancelled) return false;

    const key: StepKey = 'mail';
    const waiting = `Waiting for the send worker to mail the order's events: reading the mail sink every ${DRAIN_POLL_MS / 1000} s for up to ${DRAIN_WATCH_MS / 1000} s.`;
    this.patch(key, { state: 'running', request: 'GET /api/mail/messages', detail: waiting });

    const deadline = Date.now() + DRAIN_WATCH_MS;

    for (;;) {
      let view: MailListView;
      try {
        view = await firstValueFrom(this.host.mailMessages());
      } catch (e: unknown) {
        if (this.cancelled) return false;
        this.patch(key, { state: 'failed', status: null, body: null, detail: describe(e) });
        return false;
      }
      // A Cancel while the read was in flight ends the step without another wait.
      if (this.cancelled) return false;

      const v = mailVerdict(view, orderId, expected);
      if (v.kind === 'reached') {
        this.patch(key, { state: 'ok', detail: v.detail });
        return true;
      }
      if (v.kind === 'failed') {
        this.patch(key, { state: 'failed', detail: v.reason });
        return false;
      }
      if (Date.now() >= deadline) {
        this.patch(key, { state: 'failed', detail: `Not all sent after ${DRAIN_WATCH_MS / 1000} s. ${v.detail}` });
        return false;
      }
      this.patch(key, { detail: `${waiting} ${v.detail}` });

      await firstValueFrom(timer(DRAIN_POLL_MS).pipe(takeUntil(this.stopped)), { defaultValue: undefined });
      if (this.cancelled) return false;
    }
  }

  /**
   * Reads Inventory's reservation for the order as the user (run-locally.md) until it says Reserved, so the stock is held
   * before the cause that needs it held is applied. A 404 is the reservation not yet made and any other status than Reserved is
   * said, and both are waited out; anything else ends the step as the platform answered it.
   */
  private async reservation(read: ApiOperation, run: string, identity: Identity, orderId: string): Promise<boolean> {
    if (this.cancelled) return false;

    const key: StepKey = 'reserved';
    const correlationId = stepCorrelationId(run, key);
    const url = buildUrl(read.url, { [read.pathParameters[0]?.name ?? 'orderId']: orderId }, {});
    const waiting = `Waiting for Inventory to reserve the stock: reading the reservation every ${RESERVATION_POLL_MS / 1000} s for up to ${RESERVATION_WATCH_MS / 1000} s.`;
    this.patch(key, { state: 'running', correlationId, request: `${read.method} ${url}`, detail: waiting });

    const deadline = Date.now() + RESERVATION_WATCH_MS;
    let sentUnderId = false;

    for (;;) {
      const result = await this.exchange(key, read.method, url, '', identity, correlationId, sentUnderId);
      if (result === null) return false;
      sentUnderId = true;

      const v = reservationVerdict(result.status, result.body);
      this.patch(key, { status: result.status, body: result.body });
      if (v.kind === 'reached') {
        this.patch(key, { state: 'ok', detail: v.detail });
        return true;
      }
      if (v.kind === 'failed') {
        this.patch(key, { state: 'failed', detail: v.reason });
        return false;
      }
      if (Date.now() >= deadline) {
        this.patch(key, { state: 'failed', detail: `Not reserved after ${RESERVATION_WATCH_MS / 1000} s. ${v.detail}` });
        return false;
      }
      this.patch(key, { detail: `${waiting} ${v.detail}` });

      await firstValueFrom(timer(RESERVATION_POLL_MS).pipe(takeUntil(this.stopped)), { defaultValue: undefined });
      if (this.cancelled) return false;
    }
  }

  /**
   * Takes the change the script's cause needs only now (spec §5.3): the lease is already held, and the host adds the change to
   * it, stops the service and answers the lease as it now is. A refusal is the step's failure, said as the host said it. A 500
   * is a change that failed and was undone with the rest, or that could not be: either way the lease is the run's to restore,
   * which it already is, so nothing is set that the lease step did not set.
   */
  private async takeLateChange(script: Script): Promise<boolean> {
    const change = script.lateChange;
    if (!change || this.cancelled) return false;

    this.patch('stop', {
      state: 'running',
      detail: `Asking the host to add ${change} to the lease and waiting for it to be made. ${this.leaseNote()}`,
    });

    try {
      const held = await firstValueFrom(this.host.leaseAddChange(change));
      this.patch('stop', { state: 'ok', detail: `Held: ${held.title}. The host restores it by itself at ${held.expiresAt}.` });
      return !this.cancelled;
    } catch (e: unknown) {
      // A 409 says no lease is held: the deadline already restored it, and a lease taken since is another run's, which the restore
      // step must not end. A 500 is a change the host undid, or says it could not and keeps the lease, so the restore still runs.
      if ((e as { status?: number } | null)?.status === 409) this.leaseTaken = false;
      if (this.cancelled) return false;
      this.patch('stop', { state: 'failed', detail: describe(e) });
      return false;
    }
  }

  /** One `find` on the simulator, as the entries it logged; null once a failure has been recorded on the step. */
  private async findInCarrierLog(key: StepKey, operation: ApiOperation, body: string): Promise<CarrierEntry[] | null> {
    let result: ProxyResult;
    try {
      result = await firstValueFrom(
        this.host.proxy({ method: operation.method, url: operation.url, headers: {}, body, identity: null, correlationId: null }),
      );
    } catch (e: unknown) {
      this.patch(key, { state: 'failed', status: null, body: null, detail: describe(e) });
      return null;
    }

    if (result.outcome !== 'responded') {
      const why = result.outcome === 'unreached' ? `No answer from the carrier simulator: ${result.error}` : 'The request was refused.';
      this.patch(key, { state: 'failed', status: null, body: null, detail: why });
      return null;
    }

    this.patch(key, { status: result.status, body: result.body });
    const entries = result.status === 200 && !result.bodyTruncated ? carrierEntries(result.body) : null;
    if (entries === null) {
      this.patch(key, {
        state: 'failed',
        detail: result.bodyTruncated
          ? "The simulator's answer was longer than the proxy reads, so the log cannot be judged: its request journal has grown too large for this screen to read (clear it, or restart the carrier simulator, and run again)."
          : `The simulator answered ${result.status}, not a list of the requests it logged.`,
      });
    }

    return entries;
  }

  /**
   * Takes the script's lease (spec §5.3): the host makes its changes in order, waits for each service to be healthy, and
   * answers the lease held, which takes a minute or two for a recreated service. A refusal (another run holds the stack, or a
   * change failed and was undone) is the step's failure, said as the host said it.
   */
  private async takeLease(script: Script): Promise<boolean> {
    const lease = script.lease;
    if (!lease || this.cancelled) return false;

    this.patch('lease', {
      state: 'running',
      detail: `Asking the host to change the stack and waiting for each service to be healthy, up to 3 minutes a change. ${this.leaseNote()}`,
    });

    try {
      const held = await firstValueFrom(
        this.host.leaseAcquire({
          changes: [...lease.changes],
          holdSeconds: lease.holdSeconds,
          label: script.title,
        }),
      );
      this.leaseTaken = true;
      this.patch('lease', {
        state: 'ok',
        detail: `Held: ${held.title}. The host restores it by itself at ${held.expiresAt}.`,
      });
      return !this.cancelled;
    } catch (e: unknown) {
      // A 500 is a change that failed: the host undid it, or says it could not and keeps the lease. Only that answer can leave
      // a lease of this run's behind, so only it sends the run to the restore; a 400 or a 409 changed nothing, and a 409's lease
      // is another run's, which this one must not restore.
      if ((e as { status?: number } | null)?.status === 500) this.leaseTaken = true;
      if (this.cancelled) return false;
      this.patch('lease', { state: 'failed', detail: describe(e) });
      return false;
    }
  }

  /**
   * Ends the lease the run took, if it took one. It does not wait for the steps before it to have succeeded and is not
   * stopped by Cancel; a restore that fails is said plainly, because the lease is then still held and the
   * host retries it, and the Stack screen shows it with Restore now.
   */
  private async restoreLease(script: ScriptKey): Promise<void> {
    if (!SCRIPTS[script].steps.includes('restore')) return;

    if (!this.leaseTaken) {
      this.patch('restore', { state: 'skipped', detail: 'No lease was taken, so nothing was changed.' });
      return;
    }

    this.patch('restore', { state: 'running', detail: 'Asking the host to restore the stack and waiting for each service to be running.' });

    try {
      await firstValueFrom(this.host.leaseRestore());
      this.leaseTaken = false;
      this.patch('restore', { state: 'ok', detail: 'The stack is as the run found it.' });
    } catch (e: unknown) {
      this.patch('restore', {
        state: 'failed',
        detail: `The restore failed: ${describe(e)} The lease is still held; the host tries again every 30 seconds, and the Stack screen has Restore now.`,
      });
    }
  }

  /**
   * A cancel asked of an order that is already despatched, which the platform refuses: 422 `order.already_shipped`
   * (run-locally.md, "After despatch there is nothing to compensate"). The refusal is the pass; a cancel it accepted
   * is the failure, because it means the order was not despatched or that nothing refused it.
   */
  private async lateCancel(
    cancel: ApiOperation,
    run: string,
    identity: Identity,
    orderId: string,
  ): Promise<boolean> {
    if (this.cancelled) return false;

    const key: StepKey = 'late_cancel';
    const correlationId = stepCorrelationId(run, key);
    const url = buildUrl(cancel.url, { [cancel.pathParameters[0]?.name ?? 'id']: orderId }, {});
    this.patch(key, { state: 'running', correlationId, request: `${cancel.method} ${url}` });

    const result = await this.exchange(key, cancel.method, url, cancel.exampleBody ?? '', identity, correlationId);
    if (result === null) return false;

    const refused = isAlreadyShipped(result.status, result.body);
    this.patch(key, {
      state: refused ? 'ok' : 'failed',
      status: result.status,
      body: result.body,
      detail: refused
        ? 'Refused with 422 order.already_shipped, as an order the carrier has collected is.'
        : result.status >= 200 && result.status < 300
          ? `Answered ${result.status}: the cancel was taken, so this order was not despatched.`
          : `Answered ${result.status}, not the 422 order.already_shipped a despatched order is refused with.`,
    });
    return refused;
  }

  /**
   * run-locally.md's "Attach a product image by hand", first two calls: ask the BFF for a pre-signed PUT, then PUT the
   * image to the store. Resolves to the key to publish with, and to null once a failure has been recorded on the step.
   * The second call is the host's (`POST /api/product-images/upload`), because the proxy's body is text and the image is not.
   */
  private async upload(
    operation: ApiOperation,
    run: string,
    identity: Identity,
  ): Promise<string | null> {
    if (this.cancelled) return null;

    const key: StepKey = 'upload';
    const correlationId = stepCorrelationId(run, key);
    const url = buildUrl(operation.url, {}, {});
    this.patch(key, { state: 'running', correlationId, request: `${operation.method} ${url}` });

    const issued = await this.exchange(
      key,
      operation.method,
      url,
      operation.exampleBody ?? '',
      identity,
      correlationId,
    );
    if (issued === null) return null;
    if (issued.status < 200 || issued.status >= 300) {
      this.patch(key, {
        state: 'failed',
        status: issued.status,
        body: issued.body,
        detail: `Answered ${issued.status}.`,
      });
      return null;
    }
    const signed = uploadFrom(issued.body);
    if (signed === null) {
      this.patch(key, {
        state: 'failed',
        status: issued.status,
        body: issued.body,
        detail: `Answered ${issued.status}, but not with the upload URL and key the next steps need.`,
      });
      return null;
    }

    // The query carries the signature: it is long, expires in minutes and says nothing a reader needs.
    const target = signed.uploadUrl.split('?')[0];
    this.patch(key, { request: `PUT ${target}`, detail: 'Signed; sending the image to the store.' });
    let put: ProxyResult;
    try {
      put = await firstValueFrom(
        this.host.uploadProductImage({ uploadUrl: signed.uploadUrl, headers: signed.headers }),
      );
    } catch (e: unknown) {
      this.patch(key, { state: 'failed', status: null, body: null, detail: describe(e) });
      return null;
    }
    if (put.outcome === 'unreached') {
      this.patch(key, {
        state: 'failed',
        status: null,
        body: null,
        detail: `No answer from the object store: ${put.error}`,
      });
      return null;
    }
    if (put.outcome !== 'responded') {
      this.patch(key, { state: 'failed', status: put.status, body: put.body, detail: 'Refused.' });
      return null;
    }
    if (put.status < 200 || put.status >= 300) {
      this.patch(key, {
        state: 'failed',
        status: put.status,
        body: put.body,
        detail: `The store answered ${put.status} to the PUT.`,
      });
      return null;
    }

    this.patch(key, {
      state: 'ok',
      status: issued.status,
      body: issued.body,
      detail: `Signed ${signed.key}, and the store answered ${put.status} to the PUT.`,
    });
    return signed.key;
  }

  /**
   * The last two calls of the same paragraph: read the product back, and fetch its `thumbnailUrl` from the edge. A product with
   * no thumbnail means the publish dropped the image; an edge that answers anything but an image with `nosniff` is a store
   * a buyer would see broken, which is what this step exists to fail on.
   */
  private async thumbnail(
    operation: ApiOperation,
    run: string,
    identity: Identity,
    productId: string,
  ): Promise<boolean> {
    if (this.cancelled) return false;

    const key: StepKey = 'thumbnail';
    const correlationId = stepCorrelationId(run, key);
    const path = { [operation.pathParameters[0]?.name ?? 'id']: productId };
    const url = buildUrl(operation.url, path, {});
    this.patch(key, { state: 'running', correlationId, request: `${operation.method} ${url}` });

    const read = await this.exchange(key, operation.method, url, '', identity, correlationId);
    if (read === null) return false;
    if (read.status < 200 || read.status >= 300) {
      this.patch(key, {
        state: 'failed',
        status: read.status,
        body: read.body,
        detail: `Answered ${read.status}.`,
      });
      return false;
    }
    const thumbnail = thumbnailFrom(read.body);
    if (thumbnail === null) {
      this.patch(key, {
        state: 'failed',
        status: read.status,
        body: read.body,
        detail: 'The product has no thumbnailUrl, so the image did not attach.',
      });
      return false;
    }

    this.patch(key, { request: `HEAD ${thumbnail}`, detail: 'Read the product; asking the edge for its image.' });
    let head: ProxyResult;
    try {
      // Anonymous, as a buyer's browser is: the edge serves products/ to anyone (blueprint-backend edge.conf).
      head = await firstValueFrom(
        this.host.proxy({
          method: 'HEAD',
          url: thumbnail,
          headers: {},
          body: null,
          identity: null,
          correlationId,
        }),
      );
    } catch (e: unknown) {
      // The proxy sends only to the configured surfaces and answers 400 for any other, so a thumbnail on another
      // origin than Admin:ImageEdgeUrl is refused; any other failure is not that, and gets no such hint.
      const refused = (e as { status?: number } | null)?.status === 400;
      this.patch(key, {
        state: 'failed',
        status: null,
        body: read.body,
        detail: refused
          ? `${describe(e)} (the thumbnail's origin must be the one Admin:ImageEdgeUrl names)`
          : describe(e),
      });
      return false;
    }
    if (head.outcome !== 'responded') {
      this.patch(key, {
        state: 'failed',
        status: null,
        body: null,
        detail:
          head.outcome === 'unreached'
            ? `No answer from the edge: ${head.error}`
            : `Refused: ${head.status}.`,
      });
      return false;
    }

    const fault = imageFault(head.status, head.headers);
    this.patch(key, {
      state: fault === null ? 'ok' : 'failed',
      status: head.status,
      body: read.body,
      detail: fault ?? `The edge answered ${head.status} with an image and nosniff.`,
    });
    return fault === null;
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
    // Once cancelled only the cancel still goes out: it undoes an order already placed.
    if (this.cancelled && key !== 'cancel') return null;

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
    sagaRead?: ApiOperation,
  ): Promise<boolean> {
    if (this.cancelled) return false;

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
        this.patch(key, { status: result.status, body: result.body });
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
        // The carrier step reads the simulator's log from when this order was placed, by the platform's own clock.
        const placed = order?.timeline.placed ? Date.parse(order.timeline.placed) : Number.NaN;
        if (!Number.isNaN(placed)) this.placedAt = placed;
        if (order === null) {
          this.patch(key, {
            state: 'failed',
            status: result.status,
            body: result.body,
            detail: 'Answered 200, but not with an order read the watch can follow.',
          });
          return false;
        }

        const readAt = Date.now();
        const saga = await this.readSaga(sagaRead, orderId, identity, correlationId);
        this.sagaReads.update((all) => [
          ...all.slice(-(SAGA_READS_KEPT - 1)),
          { readAt, status: order.status, asOf: order.asOf, timeline: order.timeline, saga },
        ]);

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
      if (this.cancelled) return false;
    }
  }

  /**
   * The saga's own word on the order, read straight after the BFF's so the strip can set the two side by side.
   * It belongs to no step: a read that fails leaves no mark on one, and the strip says it could not read the
   * saga instead. Null where the catalog has no such read. A token the proxy refused is the same unreadable,
   * since the BFF read just before it used the same grant.
   */
  private async readSaga(
    read: ApiOperation | undefined,
    orderId: string,
    identity: Identity,
    correlationId: string,
  ): Promise<SagaWord | null> {
    if (!read || this.cancelled) return null;

    const url = buildUrl(read.url, { [read.pathParameters[0]?.name ?? 'id']: orderId }, {});
    try {
      const result = await firstValueFrom(
        this.host.proxy({ method: read.method, url, headers: {}, body: null, identity, correlationId }),
      );
      return result.outcome === 'responded'
        ? sagaWord(result.status, result.body)
        : { kind: 'unreadable', status: result.outcome === 'tokenRejected' ? result.status : null };
    } catch {
      return { kind: 'unreadable', status: null };
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
      // A watch's earlier read left its status, body and any mint on the step; this call has none of them.
      this.patch(key, {
        state: 'failed',
        correlationId: traced,
        status: null,
        body: null,
        regrant: null,
        detail: describe(e),
      });
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
          regrant: null,
          detail: sentBefore
            ? `Keycloak refused ${identity.username}: ${result.status}. This read was not sent.`
            : `Keycloak refused ${identity.username}: ${result.status}. Nothing was sent.`,
        });
        return null;
      case 'unreached':
        this.patch(key, {
          state: 'failed',
          correlationId: result.sent ? result.correlationId : traced,
          status: null,
          body: null,
          detail: `No answer from the platform: ${result.error}`,
          regrant: result.sent ? regrant : null,
        });
        return null;
    }

    this.sentThisRun = true;
    // The note is about the call the step last made, so a call that minted nothing clears an earlier one's.
    this.patch(key, { regrant });
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
    // `stopped` does not replay, so a Cancel while the publish was out is checked here.
    if (this.cancelled) return false;
    this.patch('drain', { state: 'running', detail: 'Polling the broker.' });

    let last: QueuesView | undefined;
    try {
      last = await lastValueFrom(
        timer(0, DRAIN_POLL_MS).pipe(
          exhaustMap(() => this.host.brokerQueues()),
          tap((view) => {
            this.queuesBefore ??= view;
            this.projection.set(view.reachable ? view.projection : null);
          }),
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
      if (!this.cancelled) this.patch('drain', { state: 'failed', detail: describe(e) });
      return false;
    }

    if (this.cancelled) return false;
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
