import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { OutputLine } from '../../core/host/host-types';
import { JobEvent, SseClient } from '../../core/host/sse-client';
import { ComposeRun } from './stack-feed';

const line = (sequence: number, text: string): OutputLine => ({ sequence, at: '', stream: 'Stderr', text });

describe('ComposeRun', () => {
  let events: Subject<JobEvent>;
  let run: ComposeRun;

  beforeEach(() => {
    events = new Subject<JobEvent>();
    TestBed.configureTestingModule({
      providers: [{ provide: SseClient, useValue: { follow: () => events.asObservable() } }],
    });
    run = TestBed.inject(ComposeRun);
  });

  it('reads each container from the lines as they arrive, and starts over for the next job', () => {
    expect(run.progress()).toBeNull();

    run.started('up-1', 'Up');
    expect(run.progress()?.services).toEqual([]);

    events.next({ kind: 'line', line: line(0, ' Container a-1  Waiting') });
    events.next({ kind: 'line', line: line(1, 'Image layer 1 of 40') });
    events.next({ kind: 'line', line: line(2, ' Container a-1  Healthy') });
    expect(run.progress()?.services.map((s) => [s.name, s.verb, s.settled])).toEqual([['a-1', 'Healthy', true]]);
    expect(run.progress()?.settled).toBe(1);

    run.started('down-1', 'Down');
    expect(run.progress()?.services).toEqual([]);
  });
});
