import { Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, of, timer } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { hostErrorText } from '../../../core/host/host-error';
import { JobSummary } from '../../../core/host/host-types';
import { injectPageJob } from '../../../workspace/page-jobs';
import { ComposeRun, StackFeed } from '../stack-feed';

/**
 * Up, Down, Down and wipe and Reset (spec §5.3), with the Scenario lease that refuses them while it is held, the
 * Compose job's progress (#368) and the console's links. A job started here becomes the page's job, which a Job output
 * widget following the page shows.
 *
 * The `down -v` confirmation lives in this component and nowhere else (widget-workspace spec §4.2), so it is empty
 * whenever the widget is created again.
 */
@Component({
  selector: 'app-stack-controls-widget',
  imports: [FormsModule],
  templateUrl: './stack-controls-widget.html',
  styleUrl: './stack-widgets.css',
  // The wipe box's input and buttons are inline and spaced by the template's own whitespace, as on the Stack screen.
  preserveWhitespaces: true,
})
export class StackControlsWidget {
  private readonly host = inject(HostClient);
  private readonly feed = inject(StackFeed);
  private readonly pageJob = injectPageJob();
  readonly run = inject(ComposeRun);

  readonly config = input<Record<string, unknown>>({});

  /** On error, fall back to undefined so the config-derived links simply do not render. */
  readonly links = toSignal(this.host.config().pipe(catchError(() => of(undefined))));

  readonly confirmText = signal('');
  readonly canWipe = computed(() => this.confirmText() === 'down -v');
  readonly error = signal<string | null>(null);

  private readonly now = toSignal(timer(0, 1000).pipe(map(() => Date.now())), { initialValue: Date.now() });

  /** The Scenario lease the host holds (spec §5.3), read with the stack, so a lease a run left behind is shown here. */
  readonly lease = computed(() => this.feed.stack.value()?.lease ?? null);
  readonly restoring = signal(false);
  readonly restoreError = signal<string | null>(null);

  constructor() {
    this.feed.stack.attach();
  }

  /** The running job's label for its own button, `Up… 0:42`; the other buttons keep theirs. */
  buttonLabel(label: string): string {
    const job = this.run.job();
    return job && job.exitCode === null && job.label === label ? `${label}… ${clock(this.now() - job.startedAt)}` : label;
  }

  /** `Up finished: exit 0 in 1:05`, shown beside the buttons for the Compose job that ended. */
  readonly outcome = computed(() => {
    const job = this.run.job();
    return job && job.exitCode !== null && job.endedAt !== null
      ? {
          ok: job.exitCode === 0,
          text: `${job.label} ${job.exitCode === 0 ? 'finished' : 'failed'}: exit ${job.exitCode} in ${clock(job.endedAt - job.startedAt)}`,
        }
      : null;
  });

  up(): void {
    this.host.backendUp().subscribe(this.started('Up'));
  }

  down(): void {
    this.host.backendDown(false).subscribe(this.started('Down'));
  }

  wipe(): void {
    if (!this.canWipe()) {
      return;
    }
    this.host.backendDown(true, this.confirmText()).subscribe(this.started('Down and wipe'));
    this.confirmText.set('');
  }

  reset(): void {
    if (!this.canWipe()) {
      return;
    }
    this.host.backendReset(this.confirmText()).subscribe(this.started('Reset'));
    this.confirmText.set('');
  }

  restoreLease(): void {
    if (this.restoring()) {
      return;
    }

    this.restoring.set(true);
    this.host.leaseRestore().subscribe({
      next: () => {
        this.restoreError.set(null);
        this.restoring.set(false);
      },
      error: (e: unknown) => {
        this.restoreError.set(hostErrorText(e, 'The host did not answer the restore.'));
        this.restoring.set(false);
      },
    });
  }

  showOutput(jobId: string): void {
    this.pageJob.show(jobId);
  }

  private started(label: string) {
    return {
      next: (job: JobSummary) => {
        this.error.set(null);
        this.run.started(job.id, label);
        this.pageJob.show(job.id);
      },
      error: (e: unknown) => this.error.set(hostErrorText(e, 'The host refused the request.')),
    };
  }
}

function clock(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}
