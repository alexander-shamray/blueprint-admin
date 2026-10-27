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
import { OutputLine } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';
import { composeEvent, composeFailed, composePlain } from './compose-json';
import { ImagePull, PullProgress, PullState, PullTracker } from './pull-progress';

/** What marks a Compose stderr line as a failure rather than progress. */
const PROBLEM = /\b(error|unhealthy|failed|denied)\b/i;

/** A line as the log shows it: Compose's JSON written back as its plain form, and whether it was a pull's progress. */
interface Entry {
  sequence: number;
  stream: OutputLine['stream'];
  text: string;
  failed: boolean;
  pull: boolean;
}

/**
 * Live output of one job. Give it a job id; it follows until the job exits. The pane is a log a screen reader can
 * read and a keyboard can scroll, but it is not live: Compose prints hundreds of lines, and announcing each would
 * drown everything else. The status beside it says only that a job started and how it ended. A job that pulls images
 * with `--progress json` shows them as a table above the log, one row each, and the log leaves out the pull's lines
 * until asked for them.
 */
@Component({
  selector: 'app-output-pane',
  host: { '[class.fill]': 'fill()' },
  template: `
    <p class="visually-hidden" role="status">{{ announcement() }}</p>
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
        <button type="button" class="pull-lines" (click)="showPullLines.set(!showPullLines())">
          @if (showPullLines()) {
            <ng-container i18n>Hide the download lines</ng-container>
          } @else {
            <ng-container i18n>Show the download lines ({{ p.lines }})</ng-container>
          }
        </button>
      </section>
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
    /* In a tile the log takes the height the table leaves, so only the log scrolls and never the tile around it. */
    :host(.fill) .pane {
      flex: 1 1 0;
      min-height: 4rem;
      max-height: none;
      margin: 0;
    }
    :host(.fill) .pull {
      flex: 0 1 auto;
      min-height: 0;
      overflow: auto;
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
  /** Set where the pane is a widget's whole body: it then takes that body's height and scrolls only its log. */
  readonly fill = input(false);
  readonly entries = signal<Entry[]>([]);
  readonly exitCode = signal<number | null>(null);
  /** The followed job's exit code, once, when the stream reports it: what a screen re-reads state on. */
  readonly exited = output<number>();
  /** Once, when the followed job's stream fails: no exit will come for it, so a screen waiting on one stops waiting. */
  readonly failed = output<void>();
  readonly error = signal<string | null>(null);
  readonly pull = signal<PullProgress | null>(null);
  readonly showPullLines = signal(false);
  readonly shown = computed(() => (this.showPullLines() ? this.entries() : this.entries().filter((e) => !e.pull)));
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
  private readonly minutes = new Intl.NumberFormat(this.locale, { style: 'unit', unit: 'minute', unitDisplay: 'long' });
  private tracker = new PullTracker();
  private subscription?: Subscription;

  /** Docker counts in decimal megabytes, so these read as its own figures do. */
  private bytes(n: number): string {
    return n >= 1e9 ? this.gigabytes.format(n / 1e9) : this.megabytes.format(n / 1e6);
  }

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());

    effect(() => {
      const id = this.jobId();
      this.subscription?.unsubscribe();
      this.entries.set([]);
      this.exitCode.set(null);
      this.error.set(null);
      this.tracker = new PullTracker();
      this.pull.set(null);
      this.showPullLines.set(false);
      if (!id) {
        return;
      }
      this.subscription = this.sse.follow(id).subscribe({
        next: (event) => {
          if (event.kind === 'line') {
            this.append(event.line);
          } else {
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
    const event = composeEvent(line.text);
    const failed = event !== null && composeFailed(event);
    // A failure stays in the log, but it is also where an image's row turns failed.
    if (pull || failed) this.pull.set(this.tracker.snapshot());

    const entry: Entry = {
      sequence: line.sequence,
      stream: line.stream,
      text: event ? composePlain(event) : line.text,
      failed,
      pull,
    };
    this.entries.update((all) => [...all, entry]);
  }
}
