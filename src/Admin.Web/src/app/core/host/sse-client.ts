import { Injectable, NgZone, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { OutputLine } from './host-types';

export type JobEvent = { kind: 'line'; line: OutputLine } | { kind: 'exited'; exitCode: number };

/**
 * One job's Server-Sent Events as an Observable. The browser's EventSource
 * reconnects on its own and resends Last-Event-ID, which the host honours;
 * `after` is for a deliberate reopen at a known sequence.
 */
@Injectable({ providedIn: 'root' })
export class SseClient {
  private readonly zone = inject(NgZone);

  follow(jobId: string, after = -1): Observable<JobEvent> {
    return new Observable<JobEvent>((subscriber) => {
      const source = new EventSource(`/api/jobs/${encodeURIComponent(jobId)}/stream?after=${after}`);

      source.addEventListener('line', (e: MessageEvent) =>
        this.zone.run(() => subscriber.next({ kind: 'line', line: JSON.parse(e.data) as OutputLine })),
      );
      source.addEventListener('exited', (e: MessageEvent) =>
        this.zone.run(() => {
          subscriber.next({ kind: 'exited', exitCode: (JSON.parse(e.data) as { exitCode: number }).exitCode });
          source.close();
          subscriber.complete();
        }),
      );

      return () => source.close();
    });
  }
}
