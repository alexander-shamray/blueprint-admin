import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, exhaustMap, of, tap, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { OutputPane } from '../../shared/output-pane/output-pane';

@Component({
  selector: 'app-stack-page',
  imports: [FormsModule, OutputPane],
  templateUrl: './stack-page.html',
  styleUrl: './stack-page.css',
  // Angular strips whitespace-only text nodes between non-inline siblings (e.g. adjacent
  // <td>s) by default, which would run the table cells' text together with no separator.
  // The spec asserts on whitespace-collapsed row text, so keep the template's whitespace.
  preserveWhitespaces: true,
})
export class StackPage {
  private readonly host = inject(HostClient);

  /** Set when a poll fails and cleared on the next successful one; the last good StackView stays on screen meanwhile. */
  readonly pollError = signal<string | null>(null);

  /**
   * Polled every 3 seconds; toSignal tears the subscription down with the component.
   * `exhaustMap` ignores ticks while a request is in flight rather than cancelling it:
   * `docker compose ps` can take longer than a tick (the host allows it 30 s), and a
   * cancelled request makes the host kill that ps, so replacing it each tick would
   * starve the poll with no answer and no error.
   * The request is caught inside the exhaustMap (not around the whole pipeline) so a transport
   * error (host restart, 500, dropped connection) does not propagate out and end the timer —
   * only a 200 with `backend.reachable: false` is a "normal" answer the template already
   * handles. A caught tick simply does not emit, leaving `stack()` at its last good value.
   */
  readonly stack = toSignal(
    timer(0, 3000).pipe(
      exhaustMap(() =>
        this.host.stack().pipe(
          tap(() => this.pollError.set(null)),
          catchError((e: unknown) => {
            this.pollError.set(this.describeError(e));
            return of();
          }),
        ),
      ),
    ),
  );

  /** On error, fall back to undefined so the config-derived links simply do not render. */
  readonly config = toSignal(this.host.config().pipe(catchError(() => of(undefined))));

  readonly jobId = signal<string | null>(null);
  readonly confirmText = signal('');
  readonly canWipe = computed(() => this.confirmText() === 'down -v');
  readonly error = signal<string | null>(null);

  up(): void {
    this.host.backendUp().subscribe(this.started);
  }

  down(): void {
    this.host.backendDown(false).subscribe(this.started);
  }

  wipe(): void {
    if (!this.canWipe()) {
      return;
    }
    this.host.backendDown(true, this.confirmText()).subscribe(this.started);
    this.confirmText.set('');
  }

  private readonly started = {
    next: (job: { id: string }) => {
      this.error.set(null);
      this.jobId.set(job.id);
    },
    error: (e: unknown) => this.error.set(this.describeError(e, 'The host refused the request.')),
  };

  /** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
  private describeError(e: unknown, fallback = 'The host did not answer.'): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? fallback;
  }
}
