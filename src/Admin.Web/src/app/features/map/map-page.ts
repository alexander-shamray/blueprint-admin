import { Component, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { PlatformMapView } from '../../core/host/host-types';

/** Each read is a `docker compose exec` the host does not keep among its jobs (spec §5.2), so a tick costs two short processes. */
export const AUTO_REFRESH_MS = 5000;

/**
 * The platform as the running stack shows it (spec §5.15): per service the routes that reach it and the queues it
 * consumes, each with its depth and the depth's change per second between two reads. Nothing here is drawn by hand;
 * the host joins its own tables with the broker's answer, and a place where they differ is listed, not resolved.
 */
@Component({
  selector: 'app-map-page',
  imports: [FormsModule],
  templateUrl: './map-page.html',
  styleUrl: './map-page.css',
  preserveWhitespaces: true,
})
export class MapPage {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  private auto?: Subscription;
  /** The previous read: depth per queue and when it was taken, so a rate can be read off two of them. */
  private previous: { at: number; depths: Map<string, number> } | null = null;

  readonly map = signal<PlatformMapView | null>(null);
  readonly mapError = signal<string | null>(null);
  readonly refreshing = signal(false);
  readonly autoRefresh = signal(false);
  /** Queue name to depth change per second since the read before; absent until two reads have listed the queue. */
  readonly rates = signal<ReadonlyMap<string, number>>(new Map());

  constructor() {
    this.destroyRef.onDestroy(() => this.auto?.unsubscribe());
    this.refresh();
  }

  refresh(): void {
    if (this.refreshing()) return;
    this.refreshing.set(true);
    this.host.platformMap().subscribe({
      next: (view) => {
        this.noteRates(view);
        this.map.set(view);
        this.mapError.set(null);
        this.refreshing.set(false);
      },
      error: (e: unknown) => {
        this.mapError.set(e instanceof Error ? e.message : 'The host did not answer.');
        this.refreshing.set(false);
      },
    });
  }

  setAutoRefresh(on: boolean): void {
    this.autoRefresh.set(on);
    this.auto?.unsubscribe();
    this.auto = on ? timer(AUTO_REFRESH_MS, AUTO_REFRESH_MS).subscribe(() => this.refresh()) : undefined;
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
