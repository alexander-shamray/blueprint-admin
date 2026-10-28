import {
  Component,
  ElementRef,
  LOCALE_ID,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
  DestroyRef,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { OutputLine, StressProgress } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';
import { composeEvent, composeFailed, composePlain } from './compose-json';
import { ComposeProgress, ComposeService, ComposeTracker } from './compose-progress';
import { K6Progress, K6Tracker } from './k6-progress';
import { ImagePull, PullProgress, PullState, PullTracker } from './pull-progress';
import { ReadinessProgress, ReadinessTracker, SurfaceState } from './readiness-progress';
import { StepRow, StepState, StepTracker } from './step-progress';

/** What marks a Compose stderr line as a failure rather than progress. */
const PROBLEM = /\b(error|unhealthy|failed|denied)\b/i;

/**
 * A line as the log shows it: Compose's JSON written back as its plain form, and whether a table above the log shows
 * it instead (a pull's progress, a container's verb).
 */
interface Entry {
  sequence: number;
  stream: OutputLine['stream'];
  text: string;
  failed: boolean;
  tabulated: boolean;
}

/**
 * Live output of one job. Give it a job id; it follows until the job exits. The pane is a log a screen reader can
 * read and a keyboard can scroll, but it is not live: Compose prints hundreds of lines, and announcing each would
 * drown everything else. The status beside it says only that a job started and how it ended. Above the log sit what
 * the job's own lines add up to: a strip of the steps of a job that runs several, a table of the images a
 * `--progress json` job pulls, and a table of the containers a Compose job brings up or takes down. The log leaves out
 * the lines those tables are made of until asked for them.
 */
@Component({
  selector: 'app-output-pane',
  host: { '[class.fill]': 'fill()' },
  template: `
    <p class="visually-hidden" role="status">{{ announcement() }}</p>
    @if (steps(); as list) {
      <ol class="steps" aria-label="Steps" i18n-aria-label>
        @for (step of list; track step.name) {
          <li [class]="step.state">
            <span class="step-name">{{ step.name }}</span>
            <span class="step-state"> {{ stepLabel(step.state) }}</span>
            @if (step.seconds !== null) {
              <span class="step-time"> {{ duration(step.seconds) }}</span>
            }
            @if (step.detail) {
              <span class="step-detail"> {{ step.detail }}</span>
            }
          </li>
        }
      </ol>
    }
    @if (pull() || containers() || k6() || readiness() || tabulatedCount() > 0) {
      <div class="tables">
        <ng-content select="[tables]" />
        @if (k6(); as k) {
          <section class="k6" aria-label="k6 run" i18n-aria-label>
            <p class="pull-summary">{{ k6Summary(k) }}</p>
            <progress [max]="k.maxUsers || 1" [value]="k.users" aria-label="Virtual users" i18n-aria-label></progress>
            @if (k.totalSeconds) {
              <progress
                [max]="k.totalSeconds"
                [value]="k.elapsedSeconds"
                aria-label="Elapsed time"
                i18n-aria-label
              ></progress>
            }
            @if (staircaseSegments(); as segments) {
              <ol class="staircase" aria-label="Staircase" i18n-aria-label>
                @for (segment of segments; track segment.label) {
                  <li [class]="segment.state" [attr.aria-current]="segment.state === 'current' ? 'step' : null">
                    {{ segment.label }}
                  </li>
                }
              </ol>
            }
          </section>
        }
        @if (readiness(); as r) {
          <section class="readiness" aria-label="Readiness" i18n-aria-label>
            <p class="pull-summary">{{ readinessSummary(r) }}</p>
            <ul class="surfaces">
              @for (surface of r.surfaces; track surface.name) {
                <li [class]="surface.state">
                  <span class="surface-name">{{ surface.name }}</span>
                  <span class="surface-state"> {{ surfaceLabel(surface.state) }}</span>
                </li>
              }
            </ul>
          </section>
        }
        @if (pull(); as p) {
          <section class="pull" aria-label="Image pulls" i18n-aria-label>
            <p class="pull-summary">{{ summary(p) }}</p>
            <table class="pull-table">
              <thead class="visually-hidden">
                <tr>
                  <th scope="col" i18n="an image pull table's column">Image</th>
                  <th scope="col" i18n="an image pull table's column">Progress</th>
                  <th scope="col" i18n="an image pull table's column">Size</th>
                  <th scope="col" i18n="an image pull table's column">State</th>
                </tr>
              </thead>
              <tbody>
                @for (image of p.images; track image.name) {
                  <tr [class]="image.state">
                    <th scope="row" class="image" [title]="image.name">{{ image.name }}</th>
                    <td class="bar">
                      <progress
                        [max]="image.total || 1"
                        [value]="image.state === 'done' ? image.total || 1 : image.current"
                        [attr.aria-label]="image.name"
                      ></progress>
                    </td>
                    <td class="size">{{ size(image) }}</td>
                    <td class="state">{{ stateLabel(image.state) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </section>
        }
        @if (containers(); as c) {
          <section class="containers" aria-label="Containers" i18n-aria-label>
            <p class="pull-summary">{{ containerSummary(c) }}</p>
            <table class="pull-table container-table">
              <thead class="visually-hidden">
                <tr>
                  <th scope="col" i18n="a container table's column">Container</th>
                  <th scope="col" i18n="a container table's column">State</th>
                  <th scope="col" i18n="a container table's column">Time</th>
                </tr>
              </thead>
              <tbody>
                @for (service of c.services; track service.name) {
                  <tr [class]="containerClass(service)">
                    <th scope="row" class="image" [title]="service.name">{{ service.name }}</th>
                    <td class="state">{{ verbLabel(service.verb) }}</td>
                    <td class="size">{{ service.seconds === null ? '' : duration(service.seconds) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </section>
        }
        <button type="button" class="pull-lines" (click)="showRawLines.set(!showRawLines())">
          @if (showRawLines()) {
            <ng-container i18n>Hide the lines behind the tables</ng-container>
          } @else {
            <ng-container i18n>Show the lines behind the tables ({{ tabulatedCount() }})</ng-container>
          }
        </button>
      </div>
    }
    <pre
      #pane
      class="pane"
      role="log"
      aria-live="off"
      aria-label="Job output"
      i18n-aria-label
      tabindex="0"
    >@for (l of shown(); track l.sequence) {<span [class.stderr]="isProblem(l)">@if (isProblem(l)) {<span class="tag">{{ stderrTag }} </span>}{{ l.text }}
</span>}@if (exitCode() !== null) {<span class="exit" i18n="a job's exit code">exited {{ exitCode() }}</span>}</pre>
    @if (error(); as e) {
      <p class="error" role="alert">{{ e }}</p>
    }
  `,
  styles: `
    :host(.fill) {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      height: 100%;
      min-height: 0;
    }
    .pane {
      max-height: 24rem;
      overflow: auto;
      background: #111;
      color: #ddd;
      padding: 0.75rem;
      font-size: 0.8rem;
    }
    /* In a tile the log takes the height the table leaves and scrolls in it; a table of more images than its share
       scrolls in its own, so the tile around them never does. */
    :host(.fill) .pane {
      flex: 1 1 0;
      min-height: 4rem;
      max-height: none;
      margin: 0;
    }
    :host(.fill) .tables {
      flex: 0 1 auto;
      min-height: 0;
      overflow: auto;
    }
    .steps {
      display: flex;
      flex-wrap: wrap;
      gap: 0.25rem 1rem;
      margin: 0;
      padding: 0;
      list-style: none;
      font-size: 0.8rem;
    }
    .steps li {
      display: flex;
      gap: 0.4rem;
    }
    .step-name {
      font-family: monospace;
    }
    .step-state,
    .step-time {
      color: var(--app-muted);
    }
    .steps .running .step-name,
    .steps .running .step-state {
      font-weight: 600;
      color: var(--mat-sys-primary);
    }
    .steps .done .step-state {
      color: #3fa66a;
    }
    .steps .failed .step-state,
    .steps .failed .step-detail {
      color: #b00;
    }
    .steps .skipped {
      opacity: 0.6;
    }
    .containers,
    .k6,
    .readiness {
      margin-top: 0.5rem;
    }
    .k6 progress {
      display: block;
      width: 100%;
      accent-color: var(--mat-sys-primary);
    }
    .staircase,
    .surfaces {
      display: flex;
      flex-wrap: wrap;
      gap: 0.25rem 1rem;
      margin: 0.25rem 0 0;
      padding: 0;
      list-style: none;
      font-size: 0.8rem;
    }
    .staircase .done,
    .surfaces .up .surface-state {
      color: #3fa66a;
    }
    .staircase .current {
      font-weight: 600;
      color: var(--mat-sys-primary);
    }
    .surface-name {
      font-family: monospace;
    }
    .surfaces .waiting .surface-state {
      color: var(--app-muted);
    }
    .surfaces .failed .surface-name,
    .surfaces .failed .surface-state {
      color: #b00;
    }
    .pull-summary {
      margin: 0 0 0.25rem;
      font-weight: 600;
    }
    .pull-table {
      width: 100%;
      border-collapse: collapse;
      font-size: 0.8rem;
    }
    .pull-table th,
    .pull-table td {
      padding: 0.2rem 0.5rem 0.2rem 0;
      border-bottom: 1px solid var(--app-border);
      text-align: left;
    }
    .pull-table .image {
      font-family: monospace;
      font-weight: normal;
      max-width: 20rem;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .pull-table .bar {
      width: 40%;
    }
    .pull-table progress {
      width: 100%;
      accent-color: var(--mat-sys-primary);
    }
    .pull-table .done progress {
      accent-color: #3fa66a;
    }
    .pull-table .size {
      font-family: monospace;
      text-align: right;
      white-space: nowrap;
    }
    .pull-table .state {
      color: var(--app-muted);
      white-space: nowrap;
    }
    .pull-table .waiting {
      color: var(--app-muted);
    }
    .pull-table .failed .state {
      color: #b00;
    }
    .pull-table .settled .state {
      color: #3fa66a;
    }
    .pull-lines {
      margin-top: 0.25rem;
      padding: 0;
      border: 0;
      background: none;
      color: var(--mat-sys-primary);
      cursor: pointer;
      font: inherit;
      font-size: 0.8rem;
    }
    .stderr {
      color: #f88;
    }
    .tag {
      font-weight: 600;
    }
    .exit {
      color: #8cf;
    }
    .error {
      color: #b00;
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
      white-space: nowrap;
    }
  `,
})
export class OutputPane {
  /** The stream's own name, like the command output it marks: never translated (AI assistant design §5.5). */
  readonly stderrTag = '[stderr]';
  readonly jobId = input<string | null>(null);
  /**
   * Set for a `docker compose` job, which writes all of its progress to stderr: a stderr line is then marked only
   * when it says something went wrong, so a healthy Up does not read as a wall of errors.
   */
  readonly composeStderr = input(false);
  /** Set where the pane is a widget's whole body: it then takes that body's height, and its parts scroll inside it. */
  readonly fill = input(false);
  /**
   * A screen's own reader for the lines of a job it knows: it says whether a line is one the screen tabulates, and
   * the pane then keeps that line behind the toggle and hands it over through `claimed`. The screen projects its
   * table into the pane as `[tables]`.
   */
  readonly claims = input<((text: string) => boolean) | null>(null);
  /**
   * Where a stress run is on its staircase (`StressRunView.progress`), which the host reads and the pane does not: the
   * staircase is the backend's and can change, so the segments are what the host has counted and nothing more.
   */
  readonly staircase = input<StressProgress | null>(null);
  readonly claimed = output<OutputLine>();
  readonly entries = signal<Entry[]>([]);
  readonly exitCode = signal<number | null>(null);
  /** The followed job's exit code, once, when the stream reports it: what a screen re-reads state on. */
  readonly exited = output<number>();
  /** Once, when the followed job's stream fails: no exit will come for it, so a screen waiting on one stops waiting. */
  readonly failed = output<void>();
  readonly error = signal<string | null>(null);
  readonly pull = signal<PullProgress | null>(null);
  readonly containers = signal<ComposeProgress | null>(null);
  /** The steps of a job that runs several, once the host has named them. */
  readonly steps = signal<StepRow[] | null>(null);
  /** The latest progress line of a k6 run, once it has printed one. */
  readonly k6 = signal<K6Progress | null>(null);
  /** The checklist of Reset's readiness wait, once the host has written its marker. */
  readonly readiness = signal<ReadinessProgress | null>(null);
  /** Each ramp and hold the host has counted, the one k6 is in marked; empty before k6 has printed a line it can read. */
  readonly staircaseSegments = computed(() => {
    const s = this.staircase();
    if (!s || !this.k6()) return null;
    // A hold is the odd segment of its step, and a ramp the even one before it; k6 at zero users after a step is over.
    const stopped = s.users === 0 && s.step > 0;
    const last = s.holding || stopped ? 2 * s.step - 1 : 2 * s.step;
    return Array.from({ length: last + 1 }, (_, i) => ({
      label:
        i % 2 === 0
          ? $localize`:a stress step's ramp:Ramp ${i / 2 + 1}:step:`
          : $localize`:a stress step's hold:Hold ${(i + 1) / 2}:step:`,
      state: i < last || stopped ? 'done' : 'current',
    }));
  });
  readonly showRawLines = signal(false);
  readonly shown = computed(() => (this.showRawLines() ? this.entries() : this.entries().filter((e) => !e.tabulated)));
  readonly tabulatedCount = computed(() => this.entries().filter((e) => e.tabulated).length);
  readonly announcement = computed(() => {
    if (!this.jobId()) return '';
    const code = this.exitCode();
    return code === null
      ? $localize`A job is running; its output is in the job output log.`
      : $localize`The job exited ${code}:exitCode:.`;
  });

  isProblem(entry: Entry): boolean {
    return entry.stream === 'Stderr' && (!this.composeStderr() || entry.failed || PROBLEM.test(entry.text));
  }

  /** Bytes so far against the size, or the size alone once pulled; `~` while a layer has not said its size. */
  size(image: ImagePull): string {
    if (image.state === 'done') return this.bytes(image.total);
    if (image.total === 0) return '';
    return `${this.bytes(image.current)} / ${image.totalKnown ? '' : '~'}${this.bytes(image.total)}`;
  }

  stateLabel(state: PullState): string {
    switch (state) {
      case 'waiting':
        return $localize`:an image pull's state:waiting`;
      case 'downloading':
        return $localize`:an image pull's state:downloading`;
      case 'extracting':
        return $localize`:an image pull's state:extracting`;
      case 'done':
        return $localize`:an image pull's state:done`;
      case 'failed':
        return $localize`:an image pull's state:failed`;
    }
  }

  stepLabel(state: StepState): string {
    switch (state) {
      case 'pending':
        return $localize`:a job step's state:pending`;
      case 'running':
        return $localize`:a job step's state:running`;
      case 'done':
        return $localize`:a job step's state:done`;
      case 'failed':
        return $localize`:a job step's state:failed`;
      case 'skipped':
        return $localize`:a job step's state:not run`;
    }
  }

  /** Compose's verb as the table words it; a verb not listed is shown as Compose printed it, in lower case. */
  verbLabel(verb: string): string {
    switch (verb) {
      case 'Waiting':
        return $localize`:a container's state:waiting`;
      case 'Creating':
        return $localize`:a container's state:creating`;
      case 'Created':
        return $localize`:a container's state:created`;
      case 'Recreate':
        return $localize`:a container's state:recreating`;
      case 'Recreated':
        return $localize`:a container's state:recreated`;
      case 'Starting':
        return $localize`:a container's state:starting`;
      case 'Started':
        return $localize`:a container's state:started`;
      case 'Running':
        return $localize`:a container's state:running`;
      case 'Healthy':
        return $localize`:a container's state:healthy`;
      case 'Stopping':
        return $localize`:a container's state:stopping`;
      case 'Stopped':
        return $localize`:a container's state:stopped`;
      case 'Removing':
        return $localize`:a container's state:removing`;
      case 'Removed':
        return $localize`:a container's state:removed`;
      case 'Exited':
        return $localize`:a container's state:exited`;
      case 'Error':
        return $localize`:a container's state:failed`;
      default:
        return verb.toLowerCase();
    }
  }

  surfaceLabel(state: SurfaceState): string {
    switch (state) {
      case 'waiting':
        return $localize`:a platform surface's readiness:waiting`;
      case 'up':
        return $localize`:a platform surface's readiness:answers`;
      case 'failed':
        return $localize`:a platform surface's readiness:did not answer`;
    }
  }

  /** How many surfaces have answered and, while the wait goes on, the time left of the cap the host wrote. */
  readinessSummary(r: ReadinessProgress): string {
    const answered = r.surfaces.filter((s) => s.state === 'up').length;
    const count = r.surfaces.length;
    if (r.surfaces.some((s) => s.state === 'failed')) {
      return $localize`Not ready: ${answered}:answered: of ${count}:count: surfaces answer`;
    }
    if (r.ended) return $localize`Ready: ${answered}:answered: of ${count}:count: surfaces answer`;
    if (this.exitCode() !== null || Number.isNaN(r.startedAt)) {
      return $localize`Surfaces answering: ${answered}:answered: of ${count}:count:`;
    }
    const left = this.clock(Math.max(0, Math.ceil((r.startedAt + r.capSeconds * 1000 - this.now()) / 1000)));
    return $localize`Surfaces answering: ${answered}:answered: of ${count}:count: · ${left}:left: left of ${this.clock(r.capSeconds)}:cap:`;
  }

  /** Elapsed time and users against the most; the length of the run only where k6 printed it. */
  k6Summary(k: K6Progress): string {
    const elapsed =
      k.totalSeconds === null
        ? this.clock(k.elapsedSeconds)
        : $localize`${this.clock(k.elapsedSeconds)}:elapsed: of ${this.clock(k.totalSeconds)}:total:`;
    const { users, maxUsers, complete, interrupted } = k;
    return $localize`k6 · ${elapsed}:elapsed: · ${users}:users: of ${maxUsers}:max: users · ${complete}:complete: iterations complete, ${interrupted}:interrupted: interrupted`;
  }

  containerClass(service: ComposeService): string {
    return service.failed ? 'failed' : service.settled ? 'settled' : 'working';
  }

  /** How many containers have nothing more to wait for. */
  containerSummary(c: ComposeProgress): string {
    const settled = c.settled;
    const count = c.services.length;
    return $localize`Containers settled: ${settled}:settled: of ${count}:count:`;
  }

  duration(seconds: number): string {
    return this.secondsFormat.format(seconds);
  }

  /** How many images are left, how much is still to come, and while it is downloading, how fast and for how long. */
  summary(p: PullProgress): string {
    const count = p.images.length;
    if (p.left === 0) return $localize`Images pulled: ${count}:count: · ${this.bytes(p.total)}:size:`;

    const approx = p.totalKnown ? '' : '~';
    const remaining = approx + this.bytes(Math.max(0, p.total - p.current));
    const size = approx + this.bytes(p.total);
    let text = $localize`Images left to pull: ${p.left}:left: of ${count}:count: · ${remaining}:remaining: of ${size}:size: to go`;
    if (p.bytesPerSecond) {
      text += ` · ${this.rate.format(p.bytesPerSecond / 1e6)}`;
      if (p.totalKnown) {
        const minutes = Math.max(1, Math.ceil((p.total - p.current) / p.bytesPerSecond / 60));
        text += ' · ' + $localize`about ${this.minutes.format(minutes)}:minutes:`;
      }
    }
    return text;
  }

  private readonly sse = inject(SseClient);
  private readonly pane = viewChild<ElementRef<HTMLElement>>('pane');
  private readonly locale = inject(LOCALE_ID);
  private readonly megabytes = new Intl.NumberFormat(this.locale, {
    style: 'unit',
    unit: 'megabyte',
    maximumFractionDigits: 0,
  });
  private readonly gigabytes = new Intl.NumberFormat(this.locale, {
    style: 'unit',
    unit: 'gigabyte',
    maximumFractionDigits: 1,
  });
  private readonly rate = new Intl.NumberFormat(this.locale, {
    style: 'unit',
    unit: 'megabyte-per-second',
    maximumFractionDigits: 1,
  });
  private readonly secondsFormat = new Intl.NumberFormat(this.locale, {
    style: 'unit',
    unit: 'second',
    unitDisplay: 'narrow',
  });
  private readonly minutes = new Intl.NumberFormat(this.locale, { style: 'unit', unit: 'minute', unitDisplay: 'long' });
  private tracker = new PullTracker();
  private composeTracker = new ComposeTracker();
  private stepTracker: StepTracker | null = null;
  private k6Tracker = new K6Tracker();
  private readinessTracker = new ReadinessTracker();
  private subscription?: Subscription;

  private readonly now = signal(Date.now());

  /** Minutes and seconds, `1:42`: the figure a countdown or an elapsed time reads as. */
  private clock(seconds: number): string {
    const whole = Math.round(seconds);
    return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
  }

  /** Docker counts in decimal megabytes, so these read as its own figures do. */
  private bytes(n: number): string {
    return n >= 1e9 ? this.gigabytes.format(n / 1e9) : this.megabytes.format(n / 1e6);
  }

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());

    // The cap's countdown redraws once a second while the wait is going on, and not otherwise.
    effect((onCleanup) => {
      const r = this.readiness();
      if (!r || r.ended || this.exitCode() !== null) return;
      this.now.set(Date.now());
      const timer = setInterval(() => this.now.set(Date.now()), 1000);
      onCleanup(() => clearInterval(timer));
    });

    effect(() => {
      const id = this.jobId();
      this.subscription?.unsubscribe();
      this.entries.set([]);
      this.exitCode.set(null);
      this.error.set(null);
      this.tracker = new PullTracker();
      this.composeTracker = new ComposeTracker();
      this.stepTracker = null;
      this.k6Tracker = new K6Tracker();
      this.readinessTracker = new ReadinessTracker();
      this.pull.set(null);
      this.containers.set(null);
      this.steps.set(null);
      this.k6.set(null);
      this.readiness.set(null);
      this.showRawLines.set(false);
      if (!id) {
        return;
      }
      this.subscription = this.sse.follow(id).subscribe({
        next: (event) => {
          if (event.kind === 'steps') {
            // Named again on every reconnect, which resumes after the last line: the tracker keeps what it has read.
            if (!this.stepTracker?.is(event.steps)) this.stepTracker = new StepTracker(event.steps);
            this.steps.set(this.stepTracker.snapshot());
          } else if (event.kind === 'line') {
            this.append(event.line);
          } else {
            if (this.stepTracker) {
              this.stepTracker.finish(event.exitCode);
              this.steps.set(this.stepTracker.snapshot());
            }
            this.exitCode.set(event.exitCode);
            this.exited.emit(event.exitCode);
          }
          queueMicrotask(() => {
            const el = this.pane()?.nativeElement;
            if (el) {
              el.scrollTop = el.scrollHeight;
            }
          });
        },
        error: (err: unknown) => {
          this.error.set(err instanceof Error ? err.message : $localize`The job stream failed.`);
          this.failed.emit();
        },
      });
    });
  }

  private append(line: OutputLine): void {
    const pull = this.tracker.add(line);
    const container = !pull && this.composeTracker.add(line);
    const progress = !pull && !container && this.k6Tracker.add(line.text);
    if (progress) this.k6.set(this.k6Tracker.snapshot());
    const wait = !pull && !container && this.readinessTracker.add(line);
    if (wait) this.readiness.set(this.readinessTracker.snapshot());
    const claimed = !pull && !container && !progress && (this.claims()?.(line.text) ?? false);
    if (claimed) this.claimed.emit(line);
    const event = composeEvent(line.text);
    const failed = event !== null && composeFailed(event);
    // A failure stays in the log, but it is also where an image's or a container's row turns failed.
    if (pull || failed) this.pull.set(this.tracker.snapshot());
    if (container || (failed && line.text.includes('Container '))) this.containers.set(this.composeTracker.snapshot());
    if (this.stepTracker) {
      this.stepTracker.add(line);
      this.steps.set(this.stepTracker.snapshot());
    }

    const entry: Entry = {
      sequence: line.sequence,
      stream: line.stream,
      text: event ? composePlain(event) : line.text,
      failed,
      tabulated: pull || container || progress || claimed || (wait && ReadinessTracker.isPoll(line.text)),
    };
    this.entries.update((all) => [...all, entry]);
  }
}
