import { DatePipe } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { EMPTY, Subject, catchError, defer, finalize, switchMap } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { TraceEventKind, TraceView } from '../../core/host/host-types';

/** The windows the select offers. The host accepts anything from 1m to 24h; these are the useful ones. */
export const WINDOWS = ['15m', '1h', '6h', '24h'] as const;

/** Each kind's word, because state is never carried by colour alone. */
const KIND_WORDS: Record<TraceEventKind, string> = {
  HttpIn: '[http]',
  Log: '[log]',
  Span: '[span]',
  Outbox: '[outbox]',
  Publish: '[publish]',
  Consume: '[consume]',
  Queued: '[queued]',
  Error: '[error]',
};

/**
 * One correlation id's timeline (spec §5.9, §6). The route param drives the load, not the button, so
 * the URL is shareable and the back button works; submitting the form navigates.
 */
@Component({
  selector: 'app-trace-page',
  imports: [DatePipe, FormsModule],
  templateUrl: './trace-page.html',
  styleUrl: './trace-page.css',
  // Keep the whitespace between adjacent <td>s so row text reads as separate cells, as the Broker screen does.
  preserveWhitespaces: true,
})
export class TracePage {
  private readonly host = inject(HostClient);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly windows = WINDOWS;
  readonly view = signal<TraceView | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly correlationId = signal('');
  readonly window = signal<string>(WINDOWS[0]);

  /** Every load request, from the route or from Reload; the latest wins. */
  private readonly loads = new Subject<string>();

  /**
   * The id in the URL drives the load, and it is watched rather than read once: navigating from one
   * id to another reuses this component, so a constructor that only read the first param would leave
   * the old timeline on screen.
   */
  constructor() {
    // switchMap, not a re-entrancy guard: a second id arriving while the first read is out must
    // abandon that read and load the new one. Dropping it would leave the URL naming an id whose
    // timeline is never fetched.
    this.loads
      .pipe(
        switchMap((id) =>
          defer(() => {
            this.loading.set(true);

            // Cleared at the start of every load, for the same reason the view is: an error left
            // over from the previous id would otherwise sit under the new URL while its read is out.
            this.error.set(null);

            // Cleared before the read, not after it fails: the template renders the view above the
            // loading branch, so keeping it would show A's timeline for the whole of B's request
            // while the URL and the input both say B. A reload of the id already on screen keeps its
            // last good timeline, as the Broker screen does.
            if (this.view()?.correlationId !== id) this.view.set(null);

            return this.host.trace(id, this.window());
          }).pipe(
            catchError((e: unknown) => {
              this.error.set(this.describeError(e));
              return EMPTY;
            }),
            // Runs on cancellation too, before the next read's defer sets it again.
            finalize(() => this.loading.set(false)),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((view) => this.view.set(view));

    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const id = params.get('correlationId') ?? '';
      if (id === '') return;
      this.correlationId.set(id);
      this.loads.next(id);
    });
  }

  /** Navigates rather than loading: the route param is the single source of what is on screen. */
  submit(): void {
    const id = this.correlationId().trim();
    if (id === '') return;
    void this.router.navigate(['/trace', id]);
  }

  /** Whether there is anything to reload: a timeline on screen, or an id typed into the input. */
  canReload(): boolean {
    return this.view() !== null || this.correlationId().trim() !== '';
  }

  kindWord(kind: TraceEventKind): string {
    return KIND_WORDS[kind];
  }

  /** Reloads the id already on screen, for when the window changes or the platform has moved on. */
  reload(): void {
    const id = this.view()?.correlationId ?? this.correlationId().trim();
    if (id !== '') this.loads.next(id);
  }

  /** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
  private describeError(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
