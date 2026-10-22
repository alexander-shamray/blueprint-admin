import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { OutputLine } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';
import { composeProgress } from '../../shared/output-pane/compose-progress';
import { PolledFeed, TabVisibility } from '../../workspace/polled-feed';

/**
 * The Stack widgets' data (widget-workspace spec §4.2): the stack, polled every 3 seconds, and the golden-signal and
 * alert strips every 15. Each polls only while a widget reading it is showing (§4.3).
 */
@Injectable({ providedIn: 'root' })
export class StackFeed {
  private readonly host = inject(HostClient);
  private readonly visible = inject(TabVisibility).visible$;
  private readonly destroyRef = inject(DestroyRef);

  readonly stack = new PolledFeed(() => this.host.stack(), 3000, this.visible, this.destroyRef);

  /**
   * The golden-signal strip (spec §5.8), on its own slower timer: every read is one Prometheus query per dashboard
   * panel through Grafana, over the dashboard's multi-minute rate windows, so a 3-second tick would ask far more often
   * than the answer can change.
   */
  readonly health = new PolledFeed(() => this.host.telemetryHealth(), 15000, this.visible, this.destroyRef);

  /** The alert strip (spec §5.8), on the signals' timer: the rules are evaluated on a minute's interval. */
  readonly alerts = new PolledFeed(() => this.host.telemetryAlerts(), 15000, this.visible, this.destroyRef);
}

/** A Compose job the Stack controls started, named for the button that started it (spec §5.3). */
export interface ComposeJob {
  id: string;
  label: string;
  startedAt: number;
  endedAt: number | null;
  exitCode: number | null;
}

/**
 * The Compose job the console started last, followed to its exit whether or not a widget shows it: a job is running
 * work, not polling (widget-workspace spec §4.3), so an Up left running on another page still frees the buttons when
 * it ends, and its per-service progress is read from its own stream.
 */
@Injectable({ providedIn: 'root' })
export class ComposeRun {
  private readonly sse = inject(SseClient);
  private subscription?: Subscription;
  private readonly lines = signal<OutputLine[]>([]);

  readonly job = signal<ComposeJob | null>(null);
  readonly running = computed(() => {
    const job = this.job();
    return job !== null && job.exitCode === null;
  });
  /** Each container at the last verb Compose printed for it. */
  readonly progress = computed(() => (this.job() ? composeProgress(this.lines()) : null));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  started(id: string, label: string): void {
    this.subscription?.unsubscribe();
    this.lines.set([]);
    this.job.set({ id, label, startedAt: Date.now(), endedAt: null, exitCode: null });
    this.subscription = this.sse.follow(id).subscribe({
      next: (event) => {
        if (event.kind === 'line') {
          this.lines.update((all) => [...all, event.line]);
        } else {
          this.job.update((job) => (job?.id === id ? { ...job, exitCode: event.exitCode, endedAt: Date.now() } : job));
        }
      },
      // No exit will come for a stream that failed, so the buttons are freed rather than held for good.
      error: () => this.job.update((job) => (job?.id === id ? null : job)),
    });
  }
}
