import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HostClient } from '../../../core/host/host-client';
import { JobSummary } from '../../../core/host/host-types';
import { OutputPane } from '../../../shared/output-pane/output-pane';
import { widgetSession } from '../../../workspace/widget-session-store';
import { BrokerFeed, describeError } from '../broker-feed';

/**
 * The BFF order projection (widget-workspace spec §5.2): the backend tool's limits, the depth of `bff-order-events`,
 * Repair, and Rebuild behind its typed confirmation. The confirmation lives here and is empty on every render (§4.2);
 * the job being shown is kept for the widget, so leaving the page and coming back finds its output.
 */
@Component({
  selector: 'app-bff-projection-widget',
  imports: [FormsModule, OutputPane],
  template: `
    @if (feed.projectionPlan(); as p) {
      <section class="projection">
        <p>
          The buyer's order list is built from the events on <code>{{ p.queue }}</code>.
          @if (depth(); as d) {
            <span class="depth" role="status" [class.drained]="d.messages === 0">
              @if (d.messages === 0) {[drained] {{ p.queue }} is empty.} @else {[waiting] {{ p.queue }} holds {{ d.messages ?? 'some' }} {{ d.messages === 1 ? 'message' : 'messages' }}.}
            </span>
          }
        </p>
        <ul class="limits">
          @for (limit of p.limits; track limit) {
            <li>{{ limit }}</li>
          }
        </ul>
        <div class="projection-actions">
          <button class="repair" (click)="repair()" title="Send what the BFF's inbox has not recorded; nothing is deleted">Repair</button>
          <span class="rebuild-box">
            <input [attr.aria-label]="'Type ' + p.rebuildConfirm + ' to confirm a rebuild'" [placeholder]="'type: ' + p.rebuildConfirm" [ngModel]="confirmText()" (ngModelChange)="confirmText.set($event)" />
            <button class="rebuild" [disabled]="!canRebuild()" (click)="rebuild()" title="Stop the BFF, delete its order projection, replay the outbox window, start the BFF again">Rebuild</button>
          </span>
        </div>
        @if (error(); as e) {
          <p class="error" role="alert">The host did not answer: {{ e }}</p>
        }
        @if (jobId(); as id) {
          <app-output-pane [jobId]="id" (exited)="ended()" />
        }
      </section>
    } @else if (feed.projectionPlan() === null) {
      <p class="empty">The host did not give the projection tool's plan.</p>
    }
  `,
  styleUrl: './broker-widgets.css',
  preserveWhitespaces: true,
})
export class BffProjectionWidget {
  private readonly host = inject(HostClient);
  readonly feed = inject(BrokerFeed);

  /** The typed confirmation: never kept, so a re-created widget starts with it empty (§4.2). */
  readonly confirmText = signal('');
  readonly jobId = widgetSession<string | null>('jobId', null);
  readonly error = signal<string | null>(null);

  readonly canRebuild = computed(() => {
    const confirm = this.feed.projectionPlan()?.rebuildConfirm;
    return confirm !== undefined && this.confirmText() === confirm;
  });

  /** The queue the BFF's projection reads, from the listing the Queues feed holds; null until it is listed. */
  readonly depth = computed(() => {
    const queue = this.feed.projectionPlan()?.queue;
    return queue ? (this.feed.queues.value()?.queues.find((q) => q.name === queue) ?? null) : null;
  });

  /** A run started here and not seen to end: while it goes the queue's depth changes, so the depth line follows it. */
  private readonly running = signal(false);

  constructor() {
    this.feed.queues.refresh();
    const destroyRef = inject(DestroyRef);
    effect((onCleanup) => {
      if (this.running()) onCleanup(this.feed.queues.attach(destroyRef));
    });
  }

  /** The run has ended, so read the queues again: otherwise the depth line keeps the queue as the run found it. */
  ended(): void {
    this.running.set(false);
    this.feed.readQueuesAgain();
  }

  /** The tool with no argument: sends what the BFF's inbox has not recorded and deletes nothing. */
  repair(): void {
    this.host.projectionRepair().subscribe({ next: (job) => this.started(job), error: (e: unknown) => this.error.set(describeError(e)) });
  }

  /** The tool with `--reset` between a stop and a start of the BFF; only with the typed confirmation, which a refusal leaves in the box so a retry is not retyped. */
  rebuild(): void {
    if (!this.canRebuild()) return;
    this.host.projectionRebuild(this.confirmText()).subscribe({
      next: (job) => {
        this.confirmText.set('');
        this.started(job);
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  /** Shows the new job's output and reads the queues now: the run's first effect on `bff-order-events` is its stop. */
  private started(job: JobSummary): void {
    this.error.set(null);
    this.jobId.set(job.id);
    this.running.set(true);
    this.feed.readQueuesAgain();
  }
}
