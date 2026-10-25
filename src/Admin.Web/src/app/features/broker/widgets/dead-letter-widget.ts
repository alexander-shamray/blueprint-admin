import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { DeadLetterInspection, DeadLetterVerb } from '../../../core/host/host-types';
import { OutputPane } from '../../../shared/output-pane/output-pane';
import { TraceLink } from '../../trace/trace-follow';
import { BrokerFeed, describeError } from '../broker-feed';

/**
 * The dead-letter workbench (widget-workspace spec §5.2): a parked queue's messages, read with the backend's tool,
 * each with a dry-run replay or discard and then, for that message and verb, the real run behind a typed
 * confirmation. Config: `queue`, the parked queue this workbench is for (Open reads it); a Queues widget's Open
 * button points a workbench on the page at the queue instead.
 *
 * Everything that unlocks a real run lives here and is empty on every render (§4.2): the typed confirmation, the
 * "dry run started" gate that arms it, and the messages themselves. A re-created workbench starts closed.
 */
@Component({
  selector: 'app-dead-letter-widget',
  imports: [FormsModule, TraceLink, OutputPane],
  template: `
    <section class="dead-letters">
      @if (feed.deadLetterPlan() === null) {
        <p class="empty" i18n>The host did not give the dead-letter tool's plan.</p>
      } @else if (openQueue(); as queue) {
        <p>
          <span i18n
            >Dead letters on <code>{{ queue }}</code
            >. The first {{ feed.deadLetterPlan()?.inspectLimit }} messages, read with the backend's
            <code>tools/dead-letters</code>, which puts each back, flagged redelivered; a deeper queue holds more than
            this shows. Each replay or discard is a dry run first; the real run writes every message it takes to a
            record file under <code>{{ feed.deadLetterPlan()?.recordDir }}</code> before it moves it. The runbook says
            when to do either.</span
          >&ngsp;<button class="close-dead-letters" (click)="close()" i18n>Close</button>
        </p>
        @if (inspecting()) {
          <p i18n>Reading…</p>
        }
        @if (error(); as e) {
          <p class="error" role="alert">{{ e }}</p>
        }
        @if (moved(); as id) {
          <p class="moved" role="status">
            <span i18n
              ><code>{{ id }}</code> went to the real run, so the listing is dropped; any other message read is still on
              the queue. Read the queue again once the run's output says it has ended.</span
            >&ngsp;<button class="reread" [disabled]="inspecting()" (click)="open(queue)" i18n>Read again</button>
          </p>
        }
        @if (inspection(); as view) {
          @if (view.error) {
            <p class="error" role="alert">{{ view.error }}</p>
          }
          @if (view.messages.length === 0 && !view.error) {
            <p i18n>No message is waiting on {{ queue }}.</p>
          }
          @for (message of view.messages; track $index) {
            <article class="dead-letter">
              <dl>
                <dt i18n>Message</dt
                ><dd
                  ><code>
                    @if (message.messageId) {
                      {{ message.messageId }}
                    } @else {
                      <ng-container i18n>(no message id)</ng-container>
                    }
                  </code></dd
                >
                @if (message.correlationId; as c) {
                  <dt i18n>Correlation</dt
                  ><dd
                    ><a [appTraceLink]="c">{{ c }}</a></dd
                  >
                }
                @if (message.messageType; as t) {
                  <dt i18n>Type</dt
                  ><dd
                    ><code>{{ t }}</code></dd
                  >
                }
                <!-- prettier-ignore -->
                @if (message.faultExceptionType; as f) {<dt i18n>Fault</dt><dd><code>{{ f }}</code>@if (message.faultMessage; as m) {: {{ m }}}</dd>}
                @if (message.reason; as r) {
                  <dt i18n>Reason</dt><dd>{{ r }}</dd>
                }
                <dt i18n>Body</dt
                ><dd
                  ><code class="body">
                    @if (message.body !== null) {
                      {{ message.body }}
                    } @else {
                      <ng-container i18n>(binary)</ng-container>
                    }
                  </code></dd
                >
              </dl>
              @if (message.messageId; as id) {
                <div class="dead-letter-actions">
                  <button
                    class="dry-replay"
                    (click)="dryRun(id, 'replay')"
                    i18n-title
                    title="Say what a replay of {{ id }} would do; nothing moves"
                    i18n
                    >Dry-run replay</button
                  >
                  <button
                    class="dry-discard"
                    (click)="dryRun(id, 'discard')"
                    i18n-title
                    title="Say what a discard of {{ id }} would do; nothing moves"
                    i18n
                    >Dry-run discard</button
                  >
                  @if (armed(); as a) {
                    @if (a.messageId === id) {
                      <span class="execute-box">
                        <input
                          [attr.aria-label]="executeLabel(a.verb, feed.deadLetterPlan()?.executeConfirm)"
                          i18n-placeholder
                          placeholder="type: {{ feed.deadLetterPlan()?.executeConfirm }}"
                          [ngModel]="executeText()"
                          (ngModelChange)="executeText.set($event)"
                        />
                        <button class="execute" [disabled]="!canExecute()" (click)="execute()">
                          @if (a.verb === 'replay') {
                            <ng-container i18n>Replay for real</ng-container>
                          } @else {
                            <ng-container i18n>Discard for real</ng-container>
                          }
                        </button>
                      </span>
                    }
                  }
                </div>
              }
            </article>
          }
        }
        @if (jobId(); as id) {
          <app-output-pane [jobId]="id" />
        }
      } @else {
        <p class="empty">
          @if (queueOfConfig(); as q) {
            <button class="open-configured" [disabled]="inspecting()" (click)="open(q)" i18n>Open {{ q }}</button>
          } @else {
            <ng-container i18n
              >No queue is open: press Open on a parked queue in a Queues widget, or set this widget's
              queue.</ng-container
            >
          }
        </p>
      }
    </section>
  `,
  styleUrl: './broker-widgets.css',
  preserveWhitespaces: true,
})
export class DeadLetterWidget {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  readonly feed = inject(BrokerFeed);
  readonly config = input<Record<string, unknown>>({});

  /** The real-run box's accessible name, with the word to type in it. */
  executeLabel = (verb: DeadLetterVerb, word: string | undefined): string =>
    verb === 'replay'
      ? $localize`Type ${word}:word: to replay for real`
      : $localize`Type ${word}:word: to discard for real`;

  /** The parked queue this widget's config names, if any. */
  readonly queueOfConfig = computed(() => {
    const queue = this.config()['queue'];
    return typeof queue === 'string' && queue.trim() !== '' ? queue.trim() : null;
  });

  /** The parked queue whose messages are open, and what the tool read from it; null while nothing is open. */
  readonly openQueue = signal<string | null>(null);
  readonly inspection = signal<DeadLetterInspection | null>(null);
  readonly inspecting = signal(false);
  readonly error = signal<string | null>(null);
  readonly jobId = signal<string | null>(null);
  /** The message a real run was sent for: the listing is dropped until the queue is read again. Null otherwise. */
  readonly moved = signal<string | null>(null);
  /** The last dry run: the real run is offered only for that message and verb, so it is always a dry run first. */
  readonly armed = signal<{ messageId: string; verb: DeadLetterVerb } | null>(null);
  readonly executeText = signal('');
  /** Which listing is open: counted up by every Open, Close and real run, so a late answer can tell it is stale. */
  private generation = 0;
  /** The queue asked for while a read was out. */
  private waiting: string | null = null;
  private destroyed = false;
  readonly canExecute = computed(() => {
    const confirm = this.feed.deadLetterPlan()?.executeConfirm;
    return this.armed() !== null && confirm !== undefined && this.executeText() === confirm;
  });

  constructor() {
    this.feed.workbenches.update((n) => n + 1);
    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.feed.workbenches.update((n) => n - 1);
    });
    // Only a request made after this workbench appeared opens it: a re-created one starts closed.
    let handled = this.feed.openRequest()?.n ?? 0;
    effect(() => {
      const request = this.feed.openRequest();
      if (request && request.n > handled) {
        handled = request.n;
        untracked(() => this.open(request.queue));
      }
    });
  }

  /** Reads a parked queue's messages with the backend's tool, which puts each back flagged redelivered. */
  open(queue: string): void {
    // A read is out: remember the latest request and open it when that read ends, rather than drop it.
    if (this.inspecting()) {
      this.waiting = queue;
      return;
    }
    this.generation++;
    this.openQueue.set(queue);
    this.inspection.set(null);
    this.armed.set(null);
    this.executeText.set('');
    this.error.set(null);
    this.jobId.set(null);
    this.moved.set(null);
    this.inspecting.set(true);
    this.host
      .deadLetters(queue)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => {
          this.inspecting.set(false);
          const next = this.waiting;
          this.waiting = null;
          if (next !== null && !this.destroyed) this.open(next);
        }),
      )
      .subscribe({
        next: (view) => this.inspection.set(view),
        error: (e: unknown) => this.error.set(describeError(e)),
      });
  }

  close(): void {
    this.generation++;
    this.waiting = null;
    this.openQueue.set(null);
    this.inspection.set(null);
    this.armed.set(null);
    this.executeText.set('');
    this.jobId.set(null);
    this.moved.set(null);
  }

  /** The tool without `--execute`: says what would move and moves nothing. Arms the real run for this message and verb. */
  dryRun(messageId: string, verb: DeadLetterVerb): void {
    const queue = this.openQueue();
    if (!queue) return;
    this.executeText.set('');
    // The answer arms the real run only for the listing it was sent against: a Close, another Open or a real run
    // since has made a new one, and a late dry run must not arm that.
    const listing = this.generation;
    this.host.deadLetterMove(queue, verb, [messageId], null).subscribe({
      next: (job) => {
        if (listing !== this.generation) return;
        this.error.set(null);
        this.jobId.set(job.id);
        this.armed.set({ messageId, verb });
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  /** The dry run's message and verb for real, with `--execute` and a record file; only with the typed confirmation. */
  execute(): void {
    const queue = this.openQueue();
    const armed = this.armed();
    if (!queue || !armed || !this.canExecute()) return;
    const listing = this.generation;
    this.host.deadLetterMove(queue, armed.verb, [armed.messageId], this.executeText()).subscribe({
      next: (job) => {
        // The move was sent whatever the operator did since, so the queues are read; but a listing that was closed
        // or replaced meanwhile is not told the old message's job.
        this.feed.readQueuesAgain();
        if (listing !== this.generation) return;
        this.generation++;
        this.error.set(null);
        this.jobId.set(job.id);
        this.armed.set(null);
        this.inspection.set(null);
        this.moved.set(armed.messageId);
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
    this.executeText.set('');
  }
}
