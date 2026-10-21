import { Component, computed, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, exhaustMap, map, of, tap, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { DoctorView } from '../../core/host/host-types';
import { composeProgress } from '../../shared/output-pane/compose-progress';
import { OutputPane } from '../../shared/output-pane/output-pane';

@Component({
  selector: 'app-stack-page',
  imports: [FormsModule, OutputPane],
  templateUrl: './stack-page.html',
  styleUrl: './stack-page.css',
  // Angular strips whitespace-only text nodes between non-inline siblings (e.g. adjacent
  // <td>s) by default, which would run the table cells' text together with no separator.
  // The spec asserts on whitespace-collapsed row text, so keep the template's whitespace.
  preserveWhitespaces: true,
})
export class StackPage {
  private readonly host = inject(HostClient);

  /** Set when a poll fails and cleared on the next successful one; the last good StackView stays on screen meanwhile. */
  readonly pollError = signal<string | null>(null);

  /**
   * Polled every 3 seconds; toSignal tears the subscription down with the component.
   * `exhaustMap` ignores ticks while a request is in flight rather than cancelling it:
   * `docker compose ps` can take longer than a tick (the host allows it 30 s), and a
   * cancelled request makes the host kill that ps, so replacing it each tick would
   * starve the poll with no answer and no error.
   * The request is caught inside the exhaustMap (not around the whole pipeline) so a transport
   * error (host restart, 500, dropped connection) does not propagate out and end the timer —
   * only a 200 with `backend.reachable: false` is a "normal" answer the template already
   * handles. A caught tick simply does not emit, leaving `stack()` at its last good value.
   */
  readonly stack = toSignal(
    timer(0, 3000).pipe(
      exhaustMap(() =>
        this.host.stack().pipe(
          tap(() => this.pollError.set(null)),
          catchError((e: unknown) => {
            this.pollError.set(this.describeError(e));
            return of();
          }),
        ),
      ),
    ),
  );

  /** Set when the strip's poll fails at the transport, cleared on the next answer; as `pollError` does it for the stack. */
  readonly healthError = signal<string | null>(null);

  /**
   * The golden-signal strip (spec §5.8), polled on its own slower timer: every read is one
   * Prometheus query per dashboard panel through Grafana, over the dashboard's multi-minute rate
   * windows, so a 3-second tick would ask far more often than the answer can change.
   */
  readonly health = toSignal(
    timer(0, 15000).pipe(
      exhaustMap(() =>
        this.host.telemetryHealth().pipe(
          tap(() => this.healthError.set(null)),
          catchError((e: unknown) => {
            this.healthError.set(this.describeError(e));
            return of();
          }),
        ),
      ),
    ),
  );

  /** Set when the alert strip's poll fails at the transport, cleared on the next answer; as `healthError` does it for the signals. */
  readonly alertsError = signal<string | null>(null);

  /**
   * The alert strip (spec §5.8), on the signals' timer for the same reason: the rules are evaluated on a minute's interval,
   * so asking every 3 seconds would only repeat the answer.
   */
  readonly alerts = toSignal(
    timer(0, 15000).pipe(
      exhaustMap(() =>
        this.host.telemetryAlerts().pipe(
          tap(() => this.alertsError.set(null)),
          catchError((e: unknown) => {
            this.alertsError.set(this.describeError(e));
            return of();
          }),
        ),
      ),
    ),
  );

  /**
   * A rate, a share and a duration as the strip's cells print them; a rate's unit is in its column header,
   * a share and a duration carry theirs in the cell, and absent is a dash, not a zero. A rate keeps two
   * significant figures, because one refusal in a 5-minute window is a few thousandths per second and
   * fixed places would print it as a zero.
   */
  rate(value: number | null): string {
    return value === null ? '—' : String(Number(value.toPrecision(2)));
  }

  ratio(value: number | null): string {
    return value === null ? '—' : `${(value * 100).toFixed(1)} %`;
  }

  duration(seconds: number | null): string {
    return seconds === null ? '—' : `${Math.round(seconds * 1000)} ms`;
  }

  /**
   * The workstation doctor (spec §5.3): read once when the screen opens and again on request, not polled.
   * Its reads are the ones a developer makes before Up, and none of them moves while the screen is open.
   */
  readonly doctor = signal<DoctorView | null>(null);
  readonly doctorError = signal<string | null>(null);
  readonly doctorReading = signal(false);

  /**
   * The device lane's three reads (spec §5.4), read when the screen opens and on request, like the doctor's. They are
   * reads of the emulator, the gateway and the realm, which a developer makes before pressing the build, and none of
   * them moves by itself; a mismatch is raised in the repository that owns it, not fixed here.
   */
  readonly deviceReads = signal<DoctorView | null>(null);
  readonly deviceReadsError = signal<string | null>(null);
  readonly deviceReading = signal(false);

  constructor() {
    this.checkWorkstation();
    this.checkDevice();
  }

  checkDevice(): void {
    if (this.deviceReading()) {
      return;
    }

    this.deviceReading.set(true);
    this.host.frontendDeviceReads().subscribe({
      next: (view) => {
        this.deviceReads.set(view);
        this.deviceReadsError.set(null);
        this.deviceReading.set(false);
      },
      error: (e: unknown) => {
        this.deviceReadsError.set(this.describeError(e));
        this.deviceReading.set(false);
      },
    });
  }

  checkWorkstation(): void {
    if (this.doctorReading()) {
      return;
    }

    this.doctorReading.set(true);
    this.host.doctor().subscribe({
      next: (view) => {
        this.doctor.set(view);
        this.doctorError.set(null);
        this.doctorReading.set(false);
      },
      error: (e: unknown) => {
        this.doctorError.set(this.describeError(e));
        this.doctorReading.set(false);
      },
    });
  }

  /** On error, fall back to undefined so the config-derived links simply do not render. */
  readonly config = toSignal(this.host.config().pipe(catchError(() => of(undefined))));

  readonly jobId = signal<string | null>(null);
  readonly confirmText = signal('');
  readonly canWipe = computed(() => this.confirmText() === 'down -v');
  readonly error = signal<string | null>(null);

  /** Whether the client's port answers: the `client` reachability entry, which is how spec §5.4's port check reaches this screen. */
  readonly clientUp = computed(() => this.stack()?.reachability.find((r) => r.name === 'client')?.up ?? false);
  readonly frontendRunning = computed(() => this.stack()?.frontend.job?.state === 'Running');

  readonly deviceBuildRunning = computed(() => this.stack()?.frontend.deviceJob?.state === 'Running');

  /**
   * The Compose job this screen started last, named for the button that started it (spec §5.3). The button shows
   * its state, and the outcome stays beside it once the job exits, so the operator need not look for the output.
   * A job opened with a "Show output" button is not this one: it has no button to report at.
   */
  readonly compose = signal<{ id: string; label: string; startedAt: number; endedAt: number | null; exitCode: number | null } | null>(null);
  private readonly now = toSignal(timer(0, 1000).pipe(map(() => Date.now())), { initialValue: Date.now() });
  private readonly pane = viewChild(OutputPane);

  readonly composeRunning = computed(() => {
    const c = this.compose();
    return c !== null && c.exitCode === null;
  });

  /** The running job's label for its own button, `Up… 0:42`; the other buttons keep theirs. */
  buttonLabel(label: string, idle = label): string {
    const c = this.compose();
    return c && c.exitCode === null && c.label === label ? `${idle}… ${this.clock(this.now() - c.startedAt)}` : idle;
  }

  /** `Up finished: exit 0 in 1:05`, shown beside the buttons for the Compose job that ended. */
  readonly outcome = computed(() => {
    const c = this.compose();
    return c && c.exitCode !== null && c.endedAt !== null
      ? { ok: c.exitCode === 0, text: `${c.label} ${c.exitCode === 0 ? 'finished' : 'failed'}: exit ${c.exitCode} in ${this.clock(c.endedAt - c.startedAt)}` }
      : null;
  });

  /** The per-service list read from the followed job's output, for the Compose job this screen started. */
  readonly progress = computed(() => {
    const c = this.compose();
    return c && c.id === this.jobId() ? composeProgress(this.pane()?.lines() ?? []) : null;
  });

  readonly composeOutput = computed(() => this.compose()?.id === this.jobId());

  jobExited(exitCode: number): void {
    const c = this.compose();
    if (c && c.id === this.jobId() && c.exitCode === null) {
      this.compose.set({ ...c, exitCode, endedAt: Date.now() });
    }
  }

  private clock(ms: number): string {
    const s = Math.max(0, Math.floor(ms / 1000));
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  }

  private startedCompose(label: string) {
    return {
      next: (job: { id: string }) => {
        this.error.set(null);
        this.jobId.set(job.id);
        this.compose.set({ id: job.id, label, startedAt: Date.now(), endedAt: null, exitCode: null });
      },
      error: this.started.error,
    };
  }

  up(): void {
    this.host.backendUp().subscribe(this.startedCompose('Up'));
  }

  down(): void {
    this.host.backendDown(false).subscribe(this.startedCompose('Down'));
  }

  wipe(): void {
    if (!this.canWipe()) {
      return;
    }
    this.host.backendDown(true, this.confirmText()).subscribe(this.startedCompose('Down and wipe'));
    this.confirmText.set('');
  }

  reset(): void {
    if (!this.canWipe()) {
      return;
    }
    this.host.backendReset(this.confirmText()).subscribe(this.startedCompose('Reset'));
    this.confirmText.set('');
  }

  startFrontend(): void {
    this.host.frontendStart().subscribe(this.started);
  }

  stopFrontend(): void {
    this.host.frontendStop().subscribe(this.started);
  }

  /** Set while a Restore is going, and the host's reason when it did not undo the lease; the lease stays held then. */
  readonly restoring = signal(false);
  readonly restoreError = signal<string | null>(null);

  /** The Scenario lease the host holds (spec §5.3), read with the stack, so a lease a run left behind is on this screen. */
  readonly lease = computed(() => this.stack()?.lease ?? null);

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
        this.restoreError.set(this.describeError(e, 'The host did not answer the restore.'));
        this.restoring.set(false);
      },
    });
  }

  /** The last load run (spec §5.3), read with the stack, so a run started elsewhere or before a reload is on this screen. */
  readonly loadRun = computed(() => this.stack()?.loadRun ?? null);
  readonly loadRunning = computed(() => this.loadRun()?.state === 'Running');

  startLoadRun(): void {
    this.host.loadRunStart().subscribe(this.started);
  }

  /** Stop answers once the run has ended, so its output is already showing; only a refusal needs saying. */
  stopLoadRun(): void {
    this.host.loadRunStop().subscribe({ error: (e: unknown) => this.error.set(this.describeError(e, 'The host refused the request.')) });
  }

  showLoadRunOutput(): void {
    const id = this.loadRun()?.id;
    if (id) {
      this.jobId.set(id);
    }
  }

  buildForDevice(): void {
    this.host.frontendDeviceBuild().subscribe(this.started);
  }

  showDeviceOutput(): void {
    const id = this.stack()?.frontend.deviceJob?.id;
    if (id) {
      this.jobId.set(id);
    }
  }

  showFrontendOutput(): void {
    const id = this.stack()?.frontend.job?.id;
    if (id) {
      this.jobId.set(id);
    }
  }

  private readonly started = {
    next: (job: { id: string }) => {
      this.error.set(null);
      this.jobId.set(job.id);
    },
    error: (e: unknown) => this.error.set(this.describeError(e, 'The host refused the request.')),
  };

  /** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
  private describeError(e: unknown, fallback = 'The host did not answer.'): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? fallback;
  }
}
