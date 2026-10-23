import { DestroyRef, Injectable, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { PolledFeed, TabVisibility } from '../../workspace/polled-feed';

/**
 * Every broker read, not queues only: each is a `docker compose exec` the host does not keep among its jobs
 * (spec §5.2), so a tick costs one short process per read and nothing that lasts.
 */
export const AUTO_REFRESH_MS = 5000;

/**
 * The Broker widgets' data (widget-workspace spec §4.2, §4.3): the four broker reads, each its own `PolledFeed`, so
 * each polls only while a widget that switched its own auto-refresh on is showing. Auto-refresh is off by default and
 * saved with the widget; a widget with it off reads once when it appears and again when its Refresh is pressed, and
 * shows whatever another widget's poll last found.
 *
 * A failed read is each feed's own `error`, set only by its own failure and cleared only by its own success, so one
 * read's success never hides another's failure; the last good listing stays meanwhile.
 */
@Injectable({ providedIn: 'root' })
export class BrokerFeed {
  private readonly host = inject(HostClient);
  private readonly visible = inject(TabVisibility).visible$;
  private readonly destroyRef = inject(DestroyRef);

  readonly queues = new PolledFeed(() => this.host.brokerQueues(), AUTO_REFRESH_MS, this.visible, this.destroyRef);
  readonly exchanges = new PolledFeed(
    () => this.host.brokerExchanges(),
    AUTO_REFRESH_MS,
    this.visible,
    this.destroyRef,
  );
  readonly bindings = new PolledFeed(() => this.host.brokerBindings(), AUTO_REFRESH_MS, this.visible, this.destroyRef);
  readonly permissions = new PolledFeed(
    () => this.host.brokerPermissions(),
    AUTO_REFRESH_MS,
    this.visible,
    this.destroyRef,
  );

  /**
   * The projection tool's plan: undefined while the host has not answered, null when it did not (the widget then has
   * no actions, and the queues say why).
   */
  readonly projectionPlan = toSignal(this.host.projectionPlan().pipe(catchError(() => of(null))));

  /** The dead-letter tool's plan: undefined while pending, null when the host did not answer (no queue offers to open). */
  readonly deadLetterPlan = toSignal(this.host.deadLetterPlan().pipe(catchError(() => of(null))));

  /** A queue read owed because something happened while a read was out: that read may predate it. */
  private readonly queuesOwed = signal(false);

  constructor() {
    effect(() => {
      if (this.queuesOwed() && !this.queues.refreshing()) {
        untracked(() => {
          this.queuesOwed.set(false);
          this.queues.refresh();
        });
      }
    });
  }

  /**
   * Reads the queues for something that just happened (a projection run starting or ending, a move sent): a read
   * still out may have read before it, and `refresh()` starts nothing while one is out, so the read is owed and
   * runs when that one lands.
   */
  readQueuesAgain(): void {
    if (this.queues.refreshing()) {
      this.queuesOwed.set(true);
    } else {
      this.queues.refresh();
    }
  }

  /** How many dead-letter workbenches are showing; the Queues widget offers Open only while there is one to open into. */
  readonly workbenches = signal(0);

  /**
   * A parked queue a Queues widget asked a workbench to open, with a counter so the same queue asked twice reads
   * again. The workbench keeps the messages and every gate that unlocks a move, so none outlives it (§4.2).
   */
  readonly openRequest = signal<{ queue: string; n: number } | null>(null);

  requestOpen(queue: string): void {
    this.openRequest.update((last) => ({ queue, n: (last?.n ?? 0) + 1 }));
  }
}

/** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
export function describeError(e: unknown): string {
  const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
  return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
}

/** The `filter` of a listing widget's config, lower-cased for a case-insensitive contains. */
export function filterOf(config: Record<string, unknown>): string {
  return typeof config['filter'] === 'string' ? config['filter'].trim().toLowerCase() : '';
}

/** The `exchangeName` of the default exchange, whose name is empty. */
export function exchangeName(name: string): string {
  return name === '' ? '(default)' : name;
}
