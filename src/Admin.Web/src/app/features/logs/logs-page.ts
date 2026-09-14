import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription, switchMap } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { OutputLine } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';

export const COMPOSE_SERVICES = [
  'sql', 'redis-cache', 'redis-coordination', 'rabbitmq', 'keycloak', 'otel-collector', 'grafana',
  'catalog-migrator', 'catalog-api', 'gateway', 'ordering-migrator', 'ordering-api', 'web-bff',
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

  readonly services = COMPOSE_SERVICES;
  readonly selected = signal<string[]>([]);
  readonly following = signal(false);
  /** The follow request is out and has not answered: Stop must be able to cancel it. */
  readonly pending = signal(false);
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
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  toggle(service: string, on: boolean): void {
    this.selected.update((all) => (on ? [...all, service] : all.filter((s) => s !== service)));
  }

  /**
   * A single subscription covers both stages: the `POST /api/logs/follow` and the SSE stream it
   * hands off to. Storing only the inner SSE subscription left the outer POST uncancellable — a
   * late response after Stop (or a superseding Follow, or component destroy) could still resurrect
   * a stream or open an orphaned second one. `switchMap` makes `stop()`/a new `follow()`/destroy
   * unsubscribe whichever stage is currently live: unsubscribing while the POST is in flight cancels
   * the HTTP request itself, and unsubscribing once the SSE stage has started closes its EventSource.
   */
  follow(): void {
    this.stop();
    this.error.set(null);
    this.lines.set([]);
    this.pending.set(true);
    this.subscription = this.host
      .followLogs(this.selected())
      .pipe(
        switchMap((job) => {
          this.pending.set(false);
          this.following.set(true);
          return this.sse.follow(job.id);
        }),
      )
      .subscribe({
        next: (event) => {
          if (event.kind === 'line') {
            this.lines.update((all) => (all.length >= MAX_LINES ? [...all.slice(1), event.line] : [...all, event.line]));
          } else {
            this.pending.set(false);
            this.following.set(false);
          }
        },
        error: (e: unknown) => {
          // Not yet following means the POST itself failed; already following means the SSE stream did.
          const fallback = this.following() ? undefined : 'The host refused the request.';
          this.error.set(this.describeError(e, fallback));
          this.pending.set(false);
          this.following.set(false);
        },
        complete: () => {
          this.pending.set(false);
          this.following.set(false);
        },
      });
  }

  stop(): void {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.pending.set(false);
    this.following.set(false);
  }

  clear(): void {
    this.lines.set([]);
  }

  isHit(line: OutputLine): boolean {
    const id = this.correlationId();
    return id.length > 0 && line.text.includes(id);
  }

  /** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
  private describeError(e: unknown, fallback = 'The host did not answer.'): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? fallback;
  }
}
