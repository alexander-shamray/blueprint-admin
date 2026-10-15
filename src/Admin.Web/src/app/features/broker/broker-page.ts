import { Component, DestroyRef, WritableSignal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { EMPTY, Observable, Subscription, catchError, finalize, of, tap, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { DeadLetterInspection, DeadLetterVerb, ExchangesView, JobSummary, PermissionsView, QueuesView } from '../../core/host/host-types';
import { DrainedIndicator } from '../../shared/drained-indicator/drained-indicator';
import { OutputPane } from '../../shared/output-pane/output-pane';

/**
 * Every read, not queues only: each is a `docker compose exec` the host does not keep among its jobs
 * (spec §5.2), so a tick costs three short processes and nothing that lasts.
 */
export const AUTO_REFRESH_MS = 5000;

@Component({
  selector: 'app-broker-page',
  imports: [FormsModule, RouterLink, DrainedIndicator, OutputPane],
  templateUrl: './broker-page.html',
  styleUrl: './broker-page.css',
  // Keep the whitespace between adjacent <td>s so row text reads as separate cells, as the Stack screen does.
  preserveWhitespaces: true,
})
export class BrokerPage {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  private auto?: Subscription;

  readonly queues = signal<QueuesView | null>(null);
  readonly exchanges = signal<ExchangesView | null>(null);
  readonly permissions = signal<PermissionsView | null>(null);
  readonly autoRefresh = signal(false);
  /** True while any read started by refresh() is still out; refresh() is not re-entrant while it is. */
  readonly refreshing = signal(false);
  private pendingReads = 0;
  /**
   * One failed-read signal per endpoint, not a shared one: each is set only by its own read's
   * failure and cleared only by that same read's success, so an exchanges or permissions failure
   * is not silently hidden by another read's success.
   * The last good listing stays on screen meanwhile.
   */
  readonly queuesError = signal<string | null>(null);
  readonly exchangesError = signal<string | null>(null);
  readonly permissionsError = signal<string | null>(null);

  /** On error, fall back to undefined so the projection panel simply does not render: the host did not answer, and the queues say so. */
  readonly plan = toSignal(this.host.projectionPlan().pipe(catchError(() => of(undefined))));
  readonly jobId = signal<string | null>(null);
  readonly confirmText = signal('');
  readonly canRebuild = computed(() => {
    const confirm = this.plan()?.rebuildConfirm;
    return confirm !== undefined && this.confirmText() === confirm;
  });
  readonly projectionError = signal<string | null>(null);
  /** The queue the BFF's projection reads, from the listing already on screen; null until it is listed. */
  readonly projectionQueue = computed(() => {
    const queue = this.plan()?.queue;
    return queue ? (this.queues()?.queues.find((q) => q.name === queue) ?? null) : null;
  });

  /** On error, undefined, so no queue offers to open: the host did not answer, and the queues say so. */
  readonly deadLetterPlan = toSignal(this.host.deadLetterPlan().pipe(catchError(() => of(undefined))));
  /** The parked queue whose messages are open, and what the tool read from it; null while nothing is open. */
  readonly openQueue = signal<string | null>(null);
  readonly inspection = signal<DeadLetterInspection | null>(null);
  readonly inspecting = signal(false);
  readonly deadLetterError = signal<string | null>(null);
  readonly deadLetterJobId = signal<string | null>(null);
  /** True once a real run is sent: what was listed may have moved, so the listing is dropped until it is read again. */
  readonly moved = signal(false);
  /** The last dry run: the real run is offered only for that message and verb, so it is always a dry run first. */
  readonly dryRun = signal<{ messageId: string; verb: DeadLetterVerb } | null>(null);
  readonly executeText = signal('');
  readonly canExecute = computed(() => {
    const confirm = this.deadLetterPlan()?.executeConfirm;
    return this.dryRun() !== null && confirm !== undefined && this.executeText() === confirm;
  });

  constructor() {
    this.destroyRef.onDestroy(() => this.auto?.unsubscribe());
    this.refresh();
  }

  /** The tool with no argument: sends what the BFF's inbox has not recorded and deletes nothing. */
  repair(): void {
    this.host.projectionRepair().subscribe(this.projectionStarted);
  }

  /** The tool with `--reset` between a stop and a start of the BFF; only with the typed confirmation. */
  rebuild(): void {
    if (!this.canRebuild()) return;
    this.host.projectionRebuild(this.confirmText()).subscribe(this.projectionStarted);
    this.confirmText.set('');
  }

  /** Reads a parked queue's messages with the backend's tool, which puts each back flagged redelivered. */
  openDeadLetters(queue: string): void {
    if (this.inspecting()) return;
    this.openQueue.set(queue);
    this.inspection.set(null);
    this.dryRun.set(null);
    this.executeText.set('');
    this.deadLetterError.set(null);
    this.deadLetterJobId.set(null);
    this.moved.set(false);
    this.inspecting.set(true);
    this.host
      .deadLetters(queue)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.inspecting.set(false)),
      )
      .subscribe({
        next: (view) => this.inspection.set(view),
        error: (e: unknown) => this.deadLetterError.set(this.describeError(e)),
      });
  }

  closeDeadLetters(): void {
    this.openQueue.set(null);
    this.inspection.set(null);
    this.dryRun.set(null);
    this.executeText.set('');
    this.deadLetterJobId.set(null);
    this.moved.set(false);
  }

  /** The tool without `--execute`: says what would move and moves nothing. Arms the real run for this message and verb. */
  dryRunMove(messageId: string, verb: DeadLetterVerb): void {
    const queue = this.openQueue();
    if (!queue) return;
    this.executeText.set('');
    this.host.deadLetterMove(queue, verb, [messageId], null).subscribe({
      next: (job) => {
        this.deadLetterError.set(null);
        this.deadLetterJobId.set(job.id);
        this.dryRun.set({ messageId, verb });
      },
      error: (e: unknown) => this.deadLetterError.set(this.describeError(e)),
    });
  }

  /** The dry run's message and verb for real, with `--execute` and a record file; only with the typed confirmation. */
  executeMove(): void {
    const queue = this.openQueue();
    const armed = this.dryRun();
    if (!queue || !armed || !this.canExecute()) return;
    this.host.deadLetterMove(queue, armed.verb, [armed.messageId], this.executeText()).subscribe({
      next: (job) => {
        this.deadLetterError.set(null);
        this.deadLetterJobId.set(job.id);
        this.dryRun.set(null);
        this.inspection.set(null);
        this.moved.set(true);
        this.refresh();
      },
      error: (e: unknown) => this.deadLetterError.set(this.describeError(e)),
    });
    this.executeText.set('');
  }

  /** While a read from an earlier call is still out, does nothing: the Refresh button is disabled meanwhile. */
  refresh(): void {
    if (this.refreshing()) return;
    this.refreshing.set(true);
    this.pendingReads = 3;
    this.withPendingCountdown(this.read(this.host.brokerQueues(), this.queuesError)).subscribe((view) => this.queues.set(view));
    this.withPendingCountdown(this.read(this.host.brokerExchanges(), this.exchangesError)).subscribe((view) => this.exchanges.set(view));
    this.withPendingCountdown(this.read(this.host.brokerPermissions(), this.permissionsError)).subscribe((view) => this.permissions.set(view));
  }

  /** A tick is a refresh(), so it starts nothing while an earlier refresh's reads are still out. */
  setAutoRefresh(on: boolean): void {
    this.autoRefresh.set(on);
    this.auto?.unsubscribe();
    this.auto = on ? timer(AUTO_REFRESH_MS, AUTO_REFRESH_MS).subscribe(() => this.refresh()) : undefined;
  }

  /** Shows the new job's output and reads the queues now: the run's first effect on `bff-order-events` is its stop. */
  private readonly projectionStarted = {
    next: (job: JobSummary) => {
      this.projectionError.set(null);
      this.jobId.set(job.id);
      this.refresh();
    },
    error: (e: unknown) => this.projectionError.set(this.describeError(e)),
  };

  exchangeName(name: string): string {
    return name === '' ? '(default)' : name;
  }

  /** A refresh() read: counts down `pendingReads` on completion or failure, via `finalize` so a failure counts down too. */
  private withPendingCountdown<T>(source: Observable<T>): Observable<T> {
    return source.pipe(
      finalize(() => {
        this.pendingReads--;
        if (this.pendingReads === 0) this.refreshing.set(false);
      }),
    );
  }

  /** Clears `error` on a successful emission and sets it on failure, so each read owns only its own signal. */
  private read<T>(request: Observable<T>, error: WritableSignal<string | null>): Observable<T> {
    return request.pipe(
      takeUntilDestroyed(this.destroyRef),
      tap(() => error.set(null)),
      catchError((e: unknown) => {
        error.set(this.describeError(e));
        return EMPTY;
      }),
    );
  }

  /** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
  private describeError(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
