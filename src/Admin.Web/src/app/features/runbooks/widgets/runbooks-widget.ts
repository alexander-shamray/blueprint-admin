import { NgTemplateOutlet } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { EMPTY, Observable, Subject, catchError, combineLatest, merge, of, switchMap, timer } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import {
  AlertStripView,
  JobSummary,
  RunbookCatalogue,
  RunbookEntry,
  RunbookPhase,
} from '../../../core/host/host-types';
import { OutputPane } from '../../../shared/output-pane/output-pane';
import { TabVisibility } from '../../../workspace/polled-feed';
import { widgetSession } from '../../../workspace/widget-session-store';
import { Block, blocks } from '../runbook-markdown';
import { RunbookSpans } from '../runbook-spans';

/** How often the alerts are read while the widget is showing: Prometheus evaluates a minute apart, so a faster read shows nothing new. */
export const ALERT_POLL_MS = 5000;

/**
 * The backend's runbooks, each with the script that causes its alert (spec §5.14; widget-workspace spec §5.2). Which
 * runbook is open, which alert is to be caused and the job being shown are the widget's session state (§4.2): they
 * outlive its component, so leaving the page and coming back finds them, and the job's output is attached again. What
 * unlocks a cause is not kept: the first press of a `confirm` cause and the typed name of a costly one are empty on
 * every render, so a cause is never one press away because a widget was re-created.
 */
@Component({
  selector: 'app-runbooks-widget',
  imports: [FormsModule, NgTemplateOutlet, OutputPane, RunbookSpans],
  templateUrl: './runbooks-widget.html',
  styleUrl: './runbooks-widget.css',
  // Keep the whitespace between adjacent <td>s so row text reads as separate cells, as the Stack screen does.
  preserveWhitespaces: true,
})
export class RunbooksWidget {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly visible = inject(TabVisibility).visible$;
  private readonly alertsNow = new Subject<void>();

  readonly catalogue = signal<RunbookCatalogue | null>(null);
  readonly loadError = signal<string | null>(null);
  readonly loading = signal(false);

  readonly config = input<Record<string, unknown>>({});
  private readonly configuredRunbook = computed(() => {
    const runbook = this.config()['runbook'];
    return typeof runbook === 'string' && runbook !== '' ? runbook : null;
  });

  readonly selected = widgetSession<string | null>('selected', null);
  readonly entry = computed<RunbookEntry | null>(
    () => this.catalogue()?.runbooks.find((r) => r.runbook === this.selected()) ?? null,
  );
  /** The alert of the phase to cause; null is the first, which the tool takes when it is told none. */
  readonly phaseAlert = widgetSession<string | null>('phaseAlert', null);
  readonly phase = computed<RunbookPhase | null>(() => {
    const entry = this.entry();
    return entry ? (entry.phases.find((p) => p.alert === this.phaseAlert()) ?? entry.phases[0] ?? null) : null;
  });

  /** A cause the tool lists as `confirm` is pressed twice: the first press arms the button, the second sends. Never kept. */
  readonly armed = signal(false);
  /** Never kept: a typed confirmation that outlived its widget would defeat what it is for. */
  readonly confirmText = signal('');
  /** A cause the tool lists as anything but `none` or `confirm` asks for the word the host names, as `down -v` does. */
  readonly needsTyping = computed(() => {
    const phase = this.phase();
    return phase !== null && phase.confirmation !== 'none' && phase.confirmation !== 'confirm';
  });
  readonly canCause = computed(() => {
    const phase = this.phase();
    return (
      this.entry()?.script === true &&
      phase !== null &&
      !this.running() &&
      (!this.needsTyping() || this.confirmText() === phase.confirmWith)
    );
  });

  /** The job being shown, kept so its output is attached again when the widget is re-created. */
  readonly jobId = widgetSession<string | null>('jobId', null);
  /** Which runbook's cause or undo `jobId` is, since the pane outlives the selection. */
  readonly jobLabel = widgetSession('jobLabel', '');
  /** A job found on re-creation is taken to be running until its output says it ended, so no second cause starts on top of it. */
  readonly running = signal(this.jobId() !== null);
  readonly jobError = signal<string | null>(null);

  readonly text = signal<Block[] | null>(null);
  readonly textError = signal<string | null>(null);

  /** The alert strip's read, repeated; null until it answers or where the host did not. */
  readonly alerts = signal<AlertStripView | null>(null);
  readonly alertsError = signal<string | null>(null);

  constructor() {
    // Read while the tab is showing: a hidden tab polls nothing (§4.3), and the widget is gone with its page.
    combineLatest([this.visible, merge(timer(0, ALERT_POLL_MS), this.alertsNow)])
      .pipe(
        switchMap(([visible]) =>
          visible
            ? this.host.telemetryAlerts().pipe(
                catchError((e: unknown) => {
                  this.alertsError.set(this.describeError(e));
                  return of(null);
                }),
              )
            : EMPTY,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((view) => {
        if (view) this.alertsError.set(null);
        this.alerts.set(view ?? this.alerts());
      });

    const name = this.selected();
    if (name !== null) this.readText(name);
    this.refresh();

    // The runbook the config names is opened when the widget appears with none open, and again when the config comes to
    // name another (the settings editor, a preset). Its inputs arrive after the constructor, so the first run only notes it.
    let seen: string | null | undefined;
    effect(() => {
      const wanted = this.configuredRunbook();
      const first = seen === undefined;
      const changed = !first && wanted !== seen;
      seen = wanted;
      if (wanted === null) return;
      if ((first && untracked(() => this.selected()) === null) || changed) untracked(() => this.select(wanted));
    });
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
    this.readText(name);
  }

  private readText(name: string): void {
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
    this.start(
      this.host.runbookCause(
        entry.runbook,
        phase.alert === entry.phases[0]?.alert ? null : phase.alert,
        phase.confirmWith,
      ),
      `cause of ${phase.alert} (${entry.runbook})`,
    );
  }

  /** The tool's `--restore`. It deletes nothing the cause did not make and is safe twice, so it takes no confirmation. */
  restore(): void {
    const entry = this.entry();
    if (!entry?.script || this.running()) return;
    this.start(this.host.runbookRestore(entry.runbook), `undo of ${entry.runbook}`);
  }

  /** The job has ended, or its stream failed so no end will come: read the alerts at once instead of at the next tick. */
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

  private start(job: Observable<JobSummary>, label: string): void {
    this.running.set(true);
    this.jobError.set(null);
    job.subscribe({
      next: (summary) => {
        this.jobLabel.set(label);
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
