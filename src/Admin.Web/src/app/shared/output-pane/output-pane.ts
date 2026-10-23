import {
  Component,
  ElementRef,
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

/** What marks a Compose stderr line as a failure rather than progress. */
const PROBLEM = /\b(error|unhealthy|failed|denied)\b/i;

/**
 * Live output of one job. Give it a job id; it follows until the job exits. The pane is a log a screen reader can
 * read and a keyboard can scroll, but it is not live: Compose prints hundreds of lines, and announcing each would
 * drown everything else. The status beside it says only that a job started and how it ended.
 */
@Component({
  selector: 'app-output-pane',
  template: `
    <p class="visually-hidden" role="status">{{ announcement() }}</p>
    <pre
      #pane
      class="pane"
      role="log"
      aria-live="off"
      aria-label="Job output"
      i18n-aria-label
      tabindex="0"
    >@for (l of lines(); track l.sequence) {<span [class.stderr]="isProblem(l)">@if (isProblem(l)) {<span class="tag">{{ stderrTag }} </span>}{{ l.text }}
</span>}@if (exitCode() !== null) {<span class="exit" i18n="a job's exit code">exited {{ exitCode() }}</span>}</pre>
    @if (error(); as e) {
      <p class="error" role="alert">{{ e }}</p>
    }
  `,
  styles: `
    .pane {
      max-height: 24rem;
      overflow: auto;
      background: #111;
      color: #ddd;
      padding: 0.75rem;
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
  readonly lines = signal<OutputLine[]>([]);
  readonly exitCode = signal<number | null>(null);
  /** The followed job's exit code, once, when the stream reports it: what a screen re-reads state on. */
  readonly exited = output<number>();
  /** Once, when the followed job's stream fails: no exit will come for it, so a screen waiting on one stops waiting. */
  readonly failed = output<void>();
  readonly error = signal<string | null>(null);
  readonly announcement = computed(() => {
    if (!this.jobId()) return '';
    const code = this.exitCode();
    return code === null
      ? $localize`A job is running; its output is in the job output log.`
      : $localize`The job exited ${code}:exitCode:.`;
  });

  isProblem(line: OutputLine): boolean {
    return line.stream === 'Stderr' && (!this.composeStderr() || PROBLEM.test(line.text));
  }

  private readonly sse = inject(SseClient);
  private readonly pane = viewChild<ElementRef<HTMLElement>>('pane');
  private subscription?: Subscription;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());

    effect(() => {
      const id = this.jobId();
      this.subscription?.unsubscribe();
      this.lines.set([]);
      this.exitCode.set(null);
      this.error.set(null);
      if (!id) {
        return;
      }
      this.subscription = this.sse.follow(id).subscribe({
        next: (event) => {
          if (event.kind === 'line') {
            this.lines.update((all) => [...all, event.line]);
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
}
