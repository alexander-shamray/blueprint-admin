import { Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, exhaustMap, firstValueFrom, of, tap, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { TranscriptEntry } from '../../core/host/host-types';

/**
 * Reading the transcript is not something it records: it holds processes and proxied requests only, so this
 * poll adds nothing to what it shows.
 */
export const TRANSCRIPT_POLL_MS = 3000;

/**
 * What the operator did this session, as a person would type it: each command the host ran and each request the
 * proxy sent (spec §5.11). The host keeps and scrubs it; this screen reads it and hands it over.
 */
@Component({
  selector: 'app-transcript-page',
  imports: [],
  templateUrl: './transcript-page.html',
  styleUrl: './transcript-page.css',
  preserveWhitespaces: true,
})
export class TranscriptPage {
  private readonly host = inject(HostClient);

  /** Set when a poll fails at the transport, cleared on the next answer; the last good transcript stays. */
  readonly pollError = signal<string | null>(null);
  readonly copied = signal(false);
  readonly copyError = signal<string | null>(null);

  readonly transcript = toSignal(
    timer(0, TRANSCRIPT_POLL_MS).pipe(
      exhaustMap(() =>
        this.host.transcript().pipe(
          tap(() => this.pollError.set(null)),
          catchError((e: unknown) => {
            this.pollError.set(this.describeError(e));
            return of();
          }),
        ),
      ),
    ),
  );

  /** Copies the host's script, not one built from the rows here, so the scrubber has the last word on it. */
  async copy(): Promise<void> {
    this.copied.set(false);
    this.copyError.set(null);
    try {
      await navigator.clipboard.writeText(await firstValueFrom(this.host.transcriptScript()));
      this.copied.set(true);
    } catch (e: unknown) {
      this.copyError.set(this.describeError(e));
    }
  }

  outcome(entry: TranscriptEntry): string {
    if (entry.kind === 'Process') return entry.settled ? `exit ${entry.exitCode}` : 'running';
    if (!entry.settled) return 'waiting';
    return entry.status === null ? 'no answer' : `HTTP ${entry.status}`;
  }

  /** Prefers a problem-detail body's `detail`/`title`, else the error's own `message`. */
  private describeError(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
