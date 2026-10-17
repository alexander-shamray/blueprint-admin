import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Observable, Subject, catchError, merge, of, switchMap, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { AlertStripView, JobSummary, RunbookCatalogue, RunbookEntry, RunbookPhase } from '../../core/host/host-types';
import { OutputPane } from '../../shared/output-pane/output-pane';
import { Block, blocks } from './runbook-markdown';
import { RunbookSpans } from './runbook-spans';

/** How often the alerts are read while the screen is open: Prometheus evaluates a minute apart, so a faster read shows nothing new. */
export const ALERT_POLL_MS = 5000;

@Component({
  selector: 'app-runbooks-page',
  imports: [FormsModule, OutputPane, RunbookSpans],
  templateUrl: './runbooks-page.html',
  styleUrl: './runbooks-page.css',
  // Keep the whitespace between adjacent <td>s so row text reads as separate cells, as the Stack screen does.
  preserveWhitespaces: true,
})
export class RunbooksPage {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly alertsNow = new Subject<void>();

  readonly catalogue = signal<RunbookCatalogue | null>(null);
  readonly loadError = signal<string | null>(null);
  readonly loading = signal(false);

  readonly selected = signal<string | null>(null);
  readonly entry = computed<RunbookEntry | null>(() => this.catalogue()?.runbooks.find((r) => r.runbook === this.selected()) ?? null);
  /** The alert of the phase to cause; null is the first, which the tool takes when it is told none. */
  readonly phaseAlert = signal<string | null>(null);
  readonly phase = computed<RunbookPhase | null>(() => {
    const entry = this.entry();
    return entry ? (entry.phases.find((p) => p.alert === this.phaseAlert()) ?? entry.phases[0] ?? null) : null;
  });

  /** A cause the tool lists as `confirm` is pressed twice: the first press arms the button, the second sends. */
  readonly armed = signal(false);
  readonly confirmText = signal('');
  /** A cause the tool lists as anything but `none` or `confirm` asks for the word the host names, as `down -v` does. */
  readonly needsTyping = computed(() => {
    const phase = this.phase();
    return phase !== null && phase.confirmation !== 'none' && phase.confirmation !== 'confirm';
  });
  readonly canCause = computed(() => {
    const phase = this.phase();
    return this.entry()?.script === true && phase !== null && !this.running() && (!this.needsTyping() || this.confirmText() === phase.confirmWith);
  });

  readonly jobId = signal<string | null>(null);
  readonly running = signal(false);
  readonly jobError = signal<string | null>(null);

  readonly text = signal<Block[] | null>(null);
  readonly textError = signal<string | null>(null);

  /** The alert strip's read, repeated; null until it answers or where the host did not. */
  readonly alerts = signal<AlertStripView | null>(null);
  readonly alertsError = signal<string | null>(null);

  constructor() {
    merge(timer(0, ALERT_POLL_MS), this.alertsNow)
      .pipe(
        switchMap(() =>
          this.host.telemetryAlerts().pipe(
            catchError((e: unknown) => {
              this.alertsError.set(this.describeError(e));
              return of(null);
            }),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((view) => {
        if (view) this.alertsError.set(null);
        this.alerts.set(view ?? this.alerts());
      });

    this.refresh();
  }

  /** Reads the listing again: a runbook added to the clone since appears here without the console changing. */
  refresh(): void {
    if (this.loading()) return;
    this.loading.set(true);
    this.host.runbooks().subscribe({
      next: (catalogue) => {
        this.loadError.set(catalogue.error);
        this.catalogue.set(catalogue);
        this.loading.set(false);
      },
      error: (e: unknown) => {
        this.loadError.set(this.describeError(e));
        this.loading.set(false);
      },
    });
  }

  select(name: string): void {
    this.selected.set(name);
    this.phaseAlert.set(null);
    this.disarm();
    this.jobError.set(null);
    this.text.set(null);
    this.textError.set(null);
    this.host.runbookText(name).subscribe({
      next: (read) => {
        if (this.selected() === name) this.text.set(blocks(read.markdown));
      },
      error: (e: unknown) => {
        if (this.selected() === name) this.textError.set(this.describeError(e));
      },
    });
  }

  choosePhase(alert: string): void {
    this.phaseAlert.set(alert);
    this.disarm();
  }

  /** The tool's `--cause`. A `confirm` phase arms on the first press and sends on the second; a typed one sends once the word matches. */
  cause(): void {
    const entry = this.entry();
    const phase = this.phase();
    if (!entry || !phase || !this.canCause()) return;
    if (phase.confirmation === 'confirm' && !this.armed()) {
      this.armed.set(true);
      return;
    }
    this.start(this.host.runbookCause(entry.runbook, phase.alert === entry.phases[0]?.alert ? null : phase.alert, phase.confirmWith));
  }

  /** The tool's `--restore`. It deletes nothing the cause did not make and is safe twice, so it takes no confirmation. */
  restore(): void {
    const entry = this.entry();
    if (!entry?.script || this.running()) return;
    this.start(this.host.runbookRestore(entry.runbook));
  }

  /** The job has ended: read the alerts at once instead of at the next tick. */
  jobEnded(): void {
    this.running.set(false);
    this.alertsNow.next();
  }

  /** What Prometheus says of an alert the runbook names, in words a colour does not carry alone. */
  alertState(name: string): string {
    const view = this.alerts();
    if (!view) return this.alertsError() ? 'unknown' : 'reading';
    if (!view.reachable) return 'unknown';
    return view.loaded.find((r) => r.name === name)?.state ?? 'not loaded';
  }

  phaseFor(alert: string): RunbookPhase | undefined {
    return this.entry()?.phases.find((p) => p.alert === alert);
  }

  /** Seconds as the minutes a person waits: the tool's deadlines are sums of evaluation intervals. */
  minutes(seconds: number): string {
    return seconds < 90 ? `${seconds} s` : `${Math.round(seconds / 60)} min`;
  }

  private start(job: Observable<JobSummary>): void {
    this.running.set(true);
    this.jobError.set(null);
    job.subscribe({
      next: (summary) => {
        this.jobId.set(summary.id);
        this.disarm();
      },
      error: (e: unknown) => {
        this.running.set(false);
        this.jobError.set(this.describeError(e));
      },
    });
  }

  private disarm(): void {
    this.armed.set(false);
    this.confirmText.set('');
  }

  /** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
  private describeError(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
