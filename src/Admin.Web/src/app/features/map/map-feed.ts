import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { tap } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { PlatformMapView } from '../../core/host/host-types';
import { PolledFeed, TabVisibility } from '../../workspace/polled-feed';

/** Each read is a `docker compose exec` the host does not keep among its jobs (spec §5.2), so a tick costs two short processes. */
export const AUTO_REFRESH_MS = 5000;

/**
 * The Platform map widget's data (widget-workspace spec §4.2, §4.3): the host's join of its own tables with the broker's
 * answer, in a `PolledFeed` that polls only while a widget with auto-refresh on is showing (off by default). The rate a
 * queue's depth is changing at is read off two successive reads, whoever asked for them, so it is kept here with the
 * previous read rather than in a widget that may come and go.
 */
@Injectable({ providedIn: 'root' })
export class MapFeed {
  private readonly host = inject(HostClient);
  /** The previous read: depth per queue and when it was taken, so a rate can be read off two of them. */
  private previous: { at: number; depths: Map<string, number> } | null = null;

  /** Queue name to depth change per second since the read before; absent until two reads have listed the queue. */
  readonly rates = signal<ReadonlyMap<string, number>>(new Map());

  readonly map = new PolledFeed(
    () => this.host.platformMap().pipe(tap((view) => this.noteRates(view))),
    AUTO_REFRESH_MS,
    inject(TabVisibility).visible$,
    inject(DestroyRef),
  );

  /** How many Map widgets are showing. */
  private showing = 0;

  /**
   * A widget is showing. The baseline for a rate lives while one is: when the last goes (the page was left), so does
   * the previous read, and a widget that returns reads a rate only between two reads it was there for, not one
   * averaged over the minutes it was away.
   */
  show(destroyRef: DestroyRef): void {
    this.showing++;
    destroyRef.onDestroy(() => {
      if (--this.showing === 0) {
        this.previous = null;
        this.rates.set(new Map());
      }
    });
  }

  /** `+0.4/s`, `0/s` or an em dash for a queue with no earlier depth to compare. */
  rateOf(queue: string): string {
    const rate = this.rates().get(queue);
    if (rate === undefined) return '—';
    return rate === 0 ? '0/s' : `${rate > 0 ? '+' : ''}${rate.toFixed(1)}/s`;
  }

  private noteRates(view: PlatformMapView): void {
    const now = Date.now();
    const depths = new Map<string, number>();
    const rates = new Map<string, number>();
    for (const service of view.services) {
      for (const queue of service.queues) {
        if (queue.messages === null) continue;
        depths.set(queue.name, queue.messages);
        const before = this.previous?.depths.get(queue.name);
        if (this.previous && before !== undefined && now > this.previous.at) {
          rates.set(queue.name, (queue.messages - before) / ((now - this.previous.at) / 1000));
        }
      }
    }
    this.previous = { at: now, depths };
    this.rates.set(rates);
  }
}
