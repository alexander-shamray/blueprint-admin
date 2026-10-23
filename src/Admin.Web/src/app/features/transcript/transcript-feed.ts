import { DestroyRef, Injectable, inject } from '@angular/core';
import { HostClient } from '../../core/host/host-client';
import { PolledFeed, TabVisibility } from '../../workspace/polled-feed';

/**
 * Reading the transcript is not something it records: it holds processes and proxied requests only, so this
 * poll adds nothing to what it shows.
 */
export const TRANSCRIPT_POLL_MS = 3000;

/**
 * The Transcript widget's data (widget-workspace spec §4.2, §4.3): what the operator did this session, read from the
 * host every 3 seconds while a Transcript widget is showing and the tab is visible. The host keeps it, and scrubs it.
 */
@Injectable({ providedIn: 'root' })
export class TranscriptFeed {
  private readonly host = inject(HostClient);

  readonly transcript = new PolledFeed(
    () => this.host.transcript(),
    TRANSCRIPT_POLL_MS,
    inject(TabVisibility).visible$,
    inject(DestroyRef),
  );
}
