import { NgTemplateOutlet } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { EMPTY, Observable, Subject, catchError, combineLatest, merge, of, switchMap, timer } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import {
  AlertStripView,
  JobSummary,
  OutputLine,
  RunbookCatalogue,
  RunbookEntry,
  RunbookPhase,
} from '../../../core/host/host-types';
import { OutputPane } from '../../../shared/output-pane/output-pane';
import { TabVisibility } from '../../../workspace/polled-feed';
import { widgetSession } from '../../../workspace/widget-session-store';
import { GameDayLineReader, Watch } from '../game-day-lines';
import { Block, blocks } from '../runbook-markdown';
import { RunbookSpans } from '../runbook-spans';

/** How often the alerts are read while the widget is showing: Prometheus evaluates a minute apart, so a faster read shows nothing new. */
export const ALERT_POLL_MS = 5000;

/** How often a countdown redraws: it shows seconds. */
export const COUNTDOWN_TICK_MS = 1000;

/** One alert a cause or restore is waiting on: still counting, settled the way it was meant to, or past its window. */
export interface Countdown {
  watch: Watch;
  /** The listing's window for the phase (`RunbookPhase`); null where the line promised none or the listing has no such phase. */
  windowSeconds: number | null;
  status: 'waiting' | 'settled' | 'overdue';
  remainingSeconds: number;
}

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

  /** The confirmation box's accessible name, with the word to type in it. */
  confirmLabel = (word: string | null): string => $localize`Type ${word}:word: to confirm the cause`;
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

  /** The tool's `causing` and `after the restore` lines of the followed job, which the pane keeps behind its toggle and hands over. */
  readonly claims = GameDayLineReader.claims;
  private reader = new GameDayLineReader();
  private readonly watches = signal<Watch[]>([]);
  /** The watches whose alert has been seen to do what was awaited: kept, since a fired alert may resolve again. */
  private readonly settled = signal<ReadonlySet<Watch>>(new Set());
  private readonly now = signal(Date.now());
  /** The alerts the followed job is waiting on, each with the time that is left of its window. */
  readonly countdowns = computed<Countdown[]>(() => {
    const now = this.now();
    const settled = this.settled();
    return this.watches().map((watch) => {
      const phase = this.catalogue()
        ?.runbooks.flatMap((r) => r.phases)
        .find((p) => p.alert === watch.alert);
      const seconds = phase ? (watch.kind === 'cause' ? phase.firesWithinSeconds : phase.clearsWithinSeconds) : null;
      const windowSeconds = watch.windowed ? seconds : null;
      const left = windowSeconds === null ? 0 : Math.ceil((watch.startedAt + windowSeconds * 1000 - now) / 1000);
      const status = settled.has(watch) ? 'settled' : windowSeconds !== null && left <= 0 ? 'overdue' : 'waiting';
      return { watch, windowSeconds, status, remainingSeconds: Math.max(0, left) };
    });
  });

  constructor() {
    // Redraw once a second while a countdown is counting, and not otherwise.
    toObservable(computed(() => this.countdowns().some((c) => c.status === 'waiting' && c.windowSeconds !== null)))
      .pipe(
        switchMap((counting) => (counting ? timer(0, COUNTDOWN_TICK_MS) : EMPTY)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.now.set(Date.now()));

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
        this.settle();
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
      $localize`cause of ${phase.alert}:alert: (${entry.runbook}:runbook:)`,
    );
  }

  /** The tool's `--restore`. It deletes nothing the cause did not make and is safe twice, so it takes no confirmation. */
  restore(): void {
    const entry = this.entry();
    if (!entry?.script || this.running()) return;
    this.start(this.host.runbookRestore(entry.runbook), $localize`undo of ${entry.runbook}:runbook:`);
  }

  /** A line of the followed job that the pane claimed: a cause or a restore now has an alert to wait on. */
  read(line: OutputLine): void {
    if (!this.reader.add(line)) return;
    this.watches.set(this.reader.snapshot());
    this.now.set(Date.now());
    this.settle();
  }

  /**
   * An awaited change seen on the strip is kept: a cause is settled once its alert reads firing, a restore once it
   * reads inactive (or the line already said so). Nothing is read from the strip that it does not already poll.
   */
  private settle(): void {
    const view = this.alerts();
    const pending = this.watches().filter((w) => !this.settled().has(w));
    const done = pending.filter((w) => {
      if (w.state === 'inactive') return true;
      const state = view?.reachable ? view.loaded.find((r) => r.name === w.alert)?.state : undefined;
      return w.kind === 'cause' ? state === 'firing' : state === 'inactive';
    });
    if (done.length > 0) this.settled.set(new Set([...this.settled(), ...done]));
  }

  /** The job has ended, or its stream failed so no end will come: read the alerts at once instead of at the next tick. */
  jobEnded(): void {
    this.running.set(false);
    this.alertsNow.next();
  }

  /** What Prometheus says of an alert the runbook names, in words a colour does not carry alone. */
  alertState(name: string): string {
    const view = this.alerts();
    if (!view) return this.alertsError() ? $localize`unknown` : $localize`reading`;
    if (!view.reachable) return $localize`unknown`;
    return view.loaded.find((r) => r.name === name)?.state ?? $localize`not loaded`;
  }

  phaseFor(alert: string): RunbookPhase | undefined {
    return this.entry()?.phases.find((p) => p.alert === alert);
  }

  /** Seconds as the minutes a person waits: the tool's deadlines are sums of evaluation intervals. */
  minutes(seconds: number): string {
    return seconds < 90 ? $localize`${seconds}:seconds: s` : $localize`${Math.round(seconds / 60)}:minutes: min`;
  }

  /** What the countdown row says: the alert, what is awaited of it, and while it counts, the time left as minutes and seconds. */
  countdownLabel(c: Countdown): string {
    const alert = c.watch.alert;
    const window = c.windowSeconds === null ? '' : this.minutes(c.windowSeconds);
    const left = `${Math.floor(c.remainingSeconds / 60)}:${String(c.remainingSeconds % 60).padStart(2, '0')}`;
    if (c.watch.kind === 'cause') {
      if (c.status === 'settled') return $localize`:a game-day cause's alert:${alert}:alert: fired`;
      if (c.windowSeconds === null) return $localize`:a game-day cause's alert:${alert}:alert: should fire`;
      return c.status === 'overdue'
        ? $localize`:a game-day cause's alert:${alert}:alert: did not fire within ${window}:window:`
        : $localize`:a game-day cause's alert:${alert}:alert: should fire within ${window}:window: (${left}:left: left)`;
    }
    if (c.status === 'settled') return $localize`:a game-day restore's alert:${alert}:alert: cleared`;
    if (c.windowSeconds === null) {
      return $localize`:a game-day restore's alert:${alert}:alert: is ${c.watch.state}:state: after the restore`;
    }
    return c.status === 'overdue'
      ? $localize`:a game-day restore's alert:${alert}:alert: did not clear within ${window}:window:`
      : $localize`:a game-day restore's alert:${alert}:alert: should clear within ${window}:window: (${left}:left: left)`;
  }

  private start(job: Observable<JobSummary>, label: string): void {
    this.running.set(true);
    this.jobError.set(null);
    job.subscribe({
      next: (summary) => {
        this.jobLabel.set(label);
        this.reader = new GameDayLineReader();
        this.watches.set([]);
        this.settled.set(new Set());
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
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? $localize`The host did not answer.`;
  }
}
