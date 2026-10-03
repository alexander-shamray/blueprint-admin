import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { EMPTY, Subscription, switchMap } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { OutputLine } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';

/**
 * The platform's Compose services, owned by blueprint-backend: every file deploy/compose/docker-compose.yml
 * includes, in its order. The drift gate reads those files against this list
 * (tests/Admin.Host.Tests/Drift/ComposeDriftTests.cs).
 */
export const COMPOSE_SERVICES = [
  'sql', 'redis-cache', 'redis-coordination', 'rabbitmq', 'keycloak', 'otel-collector', 'grafana',
  'catalog-migrator', 'catalog-api', 'gateway', 'ordering-migrator', 'ordering-api', 'web-bff',
  'inventory-migrator', 'inventory-api', 'payments-migrator', 'psp-simulator', 'payments-api',
  'shipping-migrator', 'carrier-simulator', 'shipping-worker',
  'notifications-migrator', 'mailpit', 'notifications-worker',
];

const MAX_LINES = 5000;

@Component({
  selector: 'app-logs-page',
  imports: [FormsModule],
  templateUrl: './logs-page.html',
  styleUrl: './logs-page.css',
})
export class LogsPage {
  private readonly host = inject(HostClient);
  private readonly sse = inject(SseClient);
  private subscription?: Subscription;
  /** Stop came while the follow request was out: end the job the moment the answer names it. */
  private stopOnArrival = false;
  /** The job a stop request is out for: it is not sent twice (see stopOnHost). */
  private stopping: string | null = null;

  readonly services = COMPOSE_SERVICES;
  readonly selected = signal<string[]>([]);
  readonly following = signal(false);
  /** The follow request is out and has not answered, and Stop has not been pressed since. */
  readonly pending = signal(false);
  /**
   * The host's follow job this screen started and has not seen end: Stop, and leaving the screen, end
   * it there (spec §5.10). Held until the host confirms the stop, so a stop that fails leaves Stop
   * enabled with the id to try again.
   */
  readonly hostJob = signal<string | null>(null);
  /**
   * The follow request is out, whether or not Stop has been pressed since. Only one may be out: a
   * second would cancel the first, and a cancelled request can still reach the host and start a
   * job that nothing here will ever learn the id of.
   */
  readonly requestOut = signal(false);
  readonly filter = signal('');
  readonly correlationId = signal('');
  readonly lines = signal<OutputLine[]>([]);
  /** Set when a follow request or the SSE stream fails (spec §9); cleared on the next Follow. */
  readonly error = signal<string | null>(null);

  readonly visible = computed(() => {
    const needle = this.filter().toLowerCase();
    return needle ? this.lines().filter((l) => l.text.toLowerCase().includes(needle)) : this.lines();
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  toggle(service: string, on: boolean): void {
    this.selected.update((all) => (on ? [...all, service] : all.filter((s) => s !== service)));
  }

  /**
   * A single subscription covers both stages: the `POST /api/logs/follow` and the SSE stream it
   * hands off to. The previous job is stopped by name before the next is asked for, rather than
   * left to the host's one-follow rule, which acts only once the next request arrives: a request
   * that fails on the way would otherwise leave it running. By name, a stop that lands after the
   * next follow has started ends nothing, because that follow already ended it.
   */
  follow(): void {
    if (this.requestOut()) {
      return;
    }
    this.detach();
    this.stopOnHost();
    this.stopOnArrival = false;
    this.error.set(null);
    this.lines.set([]);
    this.pending.set(true);
    this.requestOut.set(true);
    this.subscription = this.host
      .followLogs(this.selected())
      .pipe(
        switchMap((job) => {
          this.hostJob.set(job.id);
          this.requestOut.set(false);
          this.pending.set(false);
          if (this.stopOnArrival) {
            this.stopOnHost();
            return EMPTY;
          }
          this.following.set(true);
          return this.sse.follow(job.id);
        }),
      )
      .subscribe({
        next: (event) => {
          if (event.kind === 'line') {
            this.lines.update((all) => (all.length >= MAX_LINES ? [...all.slice(1), event.line] : [...all, event.line]));
          } else {
            this.hostJob.set(null);
            this.pending.set(false);
            this.following.set(false);
          }
        },
        error: (e: unknown) => {
          // Not yet following means the POST itself failed; already following means the SSE stream did.
          const fallback = this.following() ? undefined : 'The host refused the request.';
          this.error.set(this.describeError(e, fallback));
          this.requestOut.set(false);
          this.pending.set(false);
          this.following.set(false);
          // A stream that failed leaves a job this screen can neither show nor, with Stop disabled,
          // end: it is the unread logs -f of leaving the screen, and ends the same way.
          this.stopOnHost();
        },
        complete: () => {
          this.requestOut.set(false);
          this.pending.set(false);
          this.following.set(false);
          // A stream that ended leaves its job to stop. The stop-on-arrival path has already sent that
          // stop and completes through EMPTY at once: a second here would follow a first that failed
          // synchronously, after it released its claim.
          if (!this.stopOnArrival) {
            this.stopOnHost();
          }
        },
      });
  }

  /**
   * Ends the follow here and on the host. A follow request still out is left to answer rather
   * than cancelled: cancelling it cannot stop a job the host may already have started, and the
   * answer names the job to stop. That holds for every call until it answers, so leaving the
   * screen after a Stop does not detach the request and lose the id.
   */
  stop(): void {
    if (this.requestOut()) {
      this.stopOnArrival = true;
      this.pending.set(false);
      return;
    }
    this.detach();
    this.stopOnHost();
  }

  clear(): void {
    this.lines.set([]);
  }

  isHit(line: OutputLine): boolean {
    const id = this.correlationId();
    return id.length > 0 && line.text.includes(id);
  }

  private detach(): void {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.pending.set(false);
    this.following.set(false);
  }

  /**
   * Not tied to this screen's lifetime: it must still reach the host while the screen is being left.
   * The id is let go only when the host confirms, and only if it still names this job: a slow stop of
   * the previous follow must not forget the next one. A stop that fails keeps it, so Stop stays enabled
   * to try again, and its failure does not replace an error already shown, which explains the state.
   */
  private stopOnHost(): void {
    const id = this.hostJob();
    // Claimed before sending: the stop-on-arrival path, the completion that follows it, Stop and
    // teardown all reach here, and a stop still out for this id is the answer to each of them.
    if (!id || this.stopping === id) {
      return;
    }
    this.stopping = id;
    const release = (): void => {
      if (this.stopping === id) {
        this.stopping = null;
      }
    };
    this.host.stopFollow(id).subscribe({
      complete: () => {
        release();
        this.hostJob.update((held) => (held === id ? null : held));
      },
      error: (e: unknown) => {
        release();
        // Only while this job is still the one held and no follow has been asked for since: a late
        // failure to stop the previous job is not news about the follow that replaced it.
        if (this.hostJob() === id && !this.requestOut()) {
          this.error.set(this.error() ?? this.describeError(e));
        }
      },
    });
  }

  /** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
  private describeError(e: unknown, fallback = 'The host did not answer.'): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? fallback;
  }
}
