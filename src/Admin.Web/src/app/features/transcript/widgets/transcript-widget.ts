import { Component, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { TranscriptEntry } from '../../../core/host/host-types';
import { TranscriptFeed } from '../transcript-feed';

/**
 * What the operator did this session, as a person would type it: each command the host ran and each request the
 * proxy sent (spec §5.11). The host keeps and scrubs it; this widget reads it, through `TranscriptFeed`, and hands it
 * over. It has no input of its own to keep.
 */
@Component({
  selector: 'app-transcript-widget',
  templateUrl: './transcript-widget.html',
  styleUrl: './transcript-widget.css',
  preserveWhitespaces: true,
})
export class TranscriptWidget {
  private readonly host = inject(HostClient);
  readonly feed = inject(TranscriptFeed);

  readonly copied = signal(false);
  readonly copyError = signal<string | null>(null);

  constructor() {
    this.feed.transcript.attach();
  }

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
