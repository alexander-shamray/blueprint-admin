import { Injectable, computed, inject, signal } from '@angular/core';
import { EMPTY, Subscription, switchMap } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { OutputLine } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';
import { RunningWork } from '../../workspace/running-work';

/** The lines the shared buffer keeps; the oldest go first. */
export const MAX_LINES = 5000;

/**
 * The Compose service a `docker compose logs` line came from: the text before the first `|`, without the replica
 * suffix Compose adds (`catalog-api-1`). Null for a line with no prefix, which belongs to no service.
 */
export function lineService(text: string): string | null {
  const bar = text.indexOf('|');
  if (bar < 0) return null;
  return text.slice(0, bar).trim().replace(/-\d+$/, '');
}

/**
 * The one `docker compose logs -f` the console follows (widget-workspace spec §4.4, Logs), shared by every Logs
 * widget. It follows the union of the services the widgets ask for, and restarts the follow when that union changes
 * while one runs; it keeps a bounded buffer of lines; and it is ended by Stop and by nothing else, so leaving the
 * page (or a widget) leaves it following. The host allows one follow, so more than one would end the first
 * (README, Known limits). A page reload ends this service's memory of the job, not the job: the host's next follow
 * ends it, as its one-follow rule says.
 *
 * The state machine is the Logs screen's, moved here unchanged apart from what ends it: its comments carry the
 * reasons for each guard.
 */
@Injectable({ providedIn: 'root' })
export class LogFollowService {
  private readonly host = inject(HostClient);
  private readonly sse = inject(SseClient);
  private subscription?: Subscription;
  /** Stop came while the follow request was out: end the job the moment the answer names it. */
  private stopOnArrival = false;
  /** The services changed while the follow request was out: follow again once the answer names its job. */
  private restartOnArrival = false;
  /** The answer named a job that is to be replaced at once, so its completion starts the next follow. */
  private restarting = false;
  /** The job a stop request is out for: it is not sent twice (see stopOnHost). */
  private stopping: string | null = null;
  /** What the running follow was asked for, to tell when the widgets' union has moved on from it. */
  private followedKey: string | null = null;

  readonly following = signal(false);
  /** The follow request is out and has not answered, and Stop has not been pressed since. */
  readonly pending = signal(false);
  /**
   * The host's follow job this service started and has not seen end: Stop ends it there (spec §5.10). Held until
   * the host confirms the stop, so a stop that fails leaves Stop enabled with the id to try again.
   */
  readonly hostJob = signal<string | null>(null);
  /**
   * The follow request is out, whether or not Stop has been pressed since. Only one may be out: a second would
   * cancel the first, and a cancelled request can still reach the host and start a job that nothing here will ever
   * learn the id of.
   */
  readonly requestOut = signal(false);
  readonly lines = signal<OutputLine[]>([]);
  /** Set when a follow request or the SSE stream fails (spec §9); cleared on the next Follow. */
  readonly error = signal<string | null>(null);

  /** What each Logs widget asked for, by the key it asked under. `[]` is every service. */
  private readonly asked = signal<ReadonlyMap<string, readonly string[]>>(new Map());

  /** The services the next follow covers: every one when any widget asks for every one, else the union. */
  readonly services = computed<string[]>(() => {
    const lists = [...this.asked().values()];
    if (lists.length === 0 || lists.some((l) => l.length === 0)) return [];
    return [...new Set(lists.flat())].sort();
  });

  constructor() {
    inject(RunningWork).report('logs', () =>
      this.following() || this.pending() ? { text: 'Logs: following', link: '/logs' } : null,
    );
  }

  /** A Logs widget says which services it shows; call again when that changes. */
  ask(key: string, services: readonly string[]): void {
    this.asked.update((all) => new Map(all).set(key, services));
    this.reconcile();
  }

  /** A Logs widget is gone. The follow is not: only Stop ends it. */
  withdraw(key: string): void {
    this.asked.update((all) => {
      const next = new Map(all);
      next.delete(key);
      return next;
    });
    this.reconcile();
  }

  /**
   * A single subscription covers both stages: the `POST /api/logs/follow` and the SSE stream it hands off to. The
   * previous job is stopped by name before the next is asked for, rather than left to the host's one-follow rule,
   * which acts only once the next request arrives: a request that fails on the way would otherwise leave it running.
   * By name, a stop that lands after the next follow has started ends nothing, because that follow already ended it.
   */
  follow(): void {
    this.start(false);
  }

  /** `keepLines` is a restart for a changed set of services (§4.4): the lines already received stay. */
  private start(keepLines: boolean): void {
    if (this.requestOut()) {
      return;
    }
    this.detach();
    this.stopOnHost();
    this.stopOnArrival = false;
    this.restartOnArrival = false;
    this.error.set(null);
    if (!keepLines) this.lines.set([]);
    this.pending.set(true);
    this.requestOut.set(true);
    const services = this.services();
    this.followedKey = services.join('\n');
    // Held before the callbacks can run: a restart re-enters this method from the completion of a synchronous
    // answer, and its subscription is the one `detach` must find afterwards.
    const subscription = new Subscription();
    this.subscription = subscription;
    subscription.add(this.host
      .followLogs(services)
      .pipe(
        switchMap((job) => {
          this.hostJob.set(job.id);
          this.requestOut.set(false);
          this.pending.set(false);
          if (this.stopOnArrival) {
            this.stopOnHost();
            return EMPTY;
          }
          // A change that has since been undone needs no restart, and the flag does not outlive the answer.
          const restart = this.restartOnArrival && this.services().join('\n') !== this.followedKey;
          this.restartOnArrival = false;
          if (restart) {
            this.restarting = true;
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
          this.restartOnArrival = false;
          this.requestOut.set(false);
          this.pending.set(false);
          this.following.set(false);
          // A stream that failed leaves a job this service can neither show nor, with Stop disabled, end: it is
          // the unread logs -f of old, and ends the same way.
          this.stopOnHost();
        },
        complete: () => {
          this.requestOut.set(false);
          this.pending.set(false);
          this.following.set(false);
          // A stream that ended leaves its job to stop. The stop-on-arrival path has already sent that stop and
          // completes through EMPTY at once: a second here would follow a first that failed synchronously, after
          // it released its claim. The same holds for the stop a restart sends.
          if (!this.stopOnArrival && !this.restarting) {
            this.stopOnHost();
          }
          if (this.restarting) {
            this.restarting = false;
            this.start(true);
          }
        },
      }));
  }

  /**
   * Ends the follow here and on the host. A follow request still out is left to answer rather than cancelled:
   * cancelling it cannot stop a job the host may already have started, and the answer names the job to stop.
   */
  stop(): void {
    if (this.requestOut()) {
      this.stopOnArrival = true;
      this.restartOnArrival = false;
      this.pending.set(false);
      return;
    }
    this.detach();
    this.stopOnHost();
  }

  clear(): void {
    this.lines.set([]);
  }

  /**
   * A follow that is running or asked for, over services the widgets no longer ask for, is replaced by one that
   * covers them. With no widget left to ask nothing changes: the follow runs on until Stop.
   */
  private reconcile(): void {
    const live = this.following() || this.requestOut();
    if (!live || this.stopOnArrival || this.asked().size === 0) return;
    if (this.services().join('\n') === this.followedKey) return;
    if (this.requestOut()) {
      this.restartOnArrival = true;
    } else {
      this.start(true);
    }
  }

  private detach(): void {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.pending.set(false);
    this.following.set(false);
  }

  /**
   * The id is let go only when the host confirms, and only if it still names this job: a slow stop of the previous
   * follow must not forget the next one. A stop that fails keeps it, so Stop stays enabled to try again, and its
   * failure does not replace an error already shown, which explains the state.
   */
  private stopOnHost(): void {
    const id = this.hostJob();
    // Claimed before sending: the stop-on-arrival path, the completion that follows it, Stop and a restart all
    // reach here, and a stop still out for this id is the answer to each of them.
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
        // Only while this job is still the one held and no follow has been asked for since: a late failure to
        // stop the previous job is not news about the follow that replaced it.
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
