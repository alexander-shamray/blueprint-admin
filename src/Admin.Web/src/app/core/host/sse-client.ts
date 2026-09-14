import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { OutputLine } from './host-types';

export type JobEvent = { kind: 'line'; line: OutputLine } | { kind: 'exited'; exitCode: number };

/**
 * One job's Server-Sent Events as an Observable. The browser's EventSource
 * reconnects on its own to the same URL and adds Last-Event-ID; the host
 * prefers that header over `?after`, and a plain follow sends no `after` at
 * all. `after` is only for a deliberate reopen at a known sequence.
 *
 * The app is zoneless, so nothing schedules change detection when these
 * emissions land outside an Angular-aware source; a consumer must hold them
 * in a signal (or otherwise notify Angular itself) rather than a plain field.
 */
@Injectable({ providedIn: 'root' })
export class SseClient {
  follow(jobId: string, after = -1): Observable<JobEvent> {
    return new Observable<JobEvent>((subscriber) => {
      const query = after === -1 ? '' : `?after=${after}`;
      const source = new EventSource(`/api/jobs/${encodeURIComponent(jobId)}/stream${query}`);

      source.addEventListener('line', (e: MessageEvent) =>
        subscriber.next({ kind: 'line', line: JSON.parse(e.data) as OutputLine }),
      );
      source.addEventListener('exited', (e: MessageEvent) => {
        subscriber.next({ kind: 'exited', exitCode: (JSON.parse(e.data) as { exitCode: number }).exitCode });
        source.close();
        subscriber.complete();
      });

      return () => source.close();
    });
  }
}
