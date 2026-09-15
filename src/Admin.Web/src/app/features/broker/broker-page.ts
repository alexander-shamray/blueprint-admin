import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { EMPTY, Observable, Subscription, catchError, exhaustMap, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ExchangesView, PermissionsView, QueuesView } from '../../core/host/host-types';
import { DrainedIndicator } from '../../shared/drained-indicator/drained-indicator';

/**
 * Queues only: exchanges and permissions change with topology, and every read is a docker compose exec job
 * the host keeps among its last fifty.
 */
export const AUTO_REFRESH_MS = 5000;

@Component({
  selector: 'app-broker-page',
  imports: [FormsModule, DrainedIndicator],
  templateUrl: './broker-page.html',
  styleUrl: './broker-page.css',
  // Keep the whitespace between adjacent <td>s so row text reads as separate cells, as the Stack screen does.
  preserveWhitespaces: true,
})
export class BrokerPage {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  private auto?: Subscription;

  readonly queues = signal<QueuesView | null>(null);
  readonly exchanges = signal<ExchangesView | null>(null);
  readonly permissions = signal<PermissionsView | null>(null);
  readonly autoRefresh = signal(false);
  /** A failed read; the last good listing stays on screen, and the next good queue read clears it. */
  readonly error = signal<string | null>(null);

  constructor() {
    this.destroyRef.onDestroy(() => this.auto?.unsubscribe());
    this.refresh();
  }

  refresh(): void {
    this.read(this.host.brokerQueues()).subscribe((view) => {
      this.error.set(null);
      this.queues.set(view);
    });
    this.read(this.host.brokerExchanges()).subscribe((view) => this.exchanges.set(view));
    this.read(this.host.brokerPermissions()).subscribe((view) => this.permissions.set(view));
  }

  /** `exhaustMap` skips a tick while a read is out: a slow rabbitmqctl must not queue reads behind it. */
  setAutoRefresh(on: boolean): void {
    this.autoRefresh.set(on);
    this.auto?.unsubscribe();
    this.auto = on
      ? timer(AUTO_REFRESH_MS, AUTO_REFRESH_MS)
          .pipe(exhaustMap(() => this.read(this.host.brokerQueues())))
          .subscribe((view) => {
            this.error.set(null);
            this.queues.set(view);
          })
      : undefined;
  }

  exchangeName(name: string): string {
    return name === '' ? '(default)' : name;
  }

  private read<T>(request: Observable<T>): Observable<T> {
    return request.pipe(
      takeUntilDestroyed(this.destroyRef),
      catchError((e: unknown) => {
        this.error.set(this.describeError(e));
        return EMPTY;
      }),
    );
  }

  /** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
  private describeError(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
