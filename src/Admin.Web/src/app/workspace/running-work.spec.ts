import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { JobEvent, SseClient } from '../core/host/sse-client';
import { ComposeRun } from '../features/stack/stack-feed';
import { RunningWork } from './running-work';

describe('RunningWork', () => {
  it('lists what each source says is running, and nothing for a source that is idle', () => {
    const work = TestBed.inject(RunningWork);
    const busy = signal(false);
    work.report('a', () => (busy() ? { text: 'A: running', link: '/a' } : null));
    work.report('b', () => ({ text: 'B: running', link: '/b' }));

    expect(work.items()).toEqual([{ text: 'B: running', link: '/b' }]);

    busy.set(true);
    expect(work.items().map((i) => i.text)).toEqual(['A: running', 'B: running']);
  });

  it('says a Compose job the Stack controls started is running until it exits, linking to the Stack page', () => {
    const events = new Subject<JobEvent>();
    TestBed.configureTestingModule({
      providers: [{ provide: SseClient, useValue: { follow: () => events.asObservable() } }],
    });
    const run = TestBed.inject(ComposeRun);
    const work = TestBed.inject(RunningWork);
    expect(work.items()).toEqual([]);

    run.started('up-1', 'Up');
    expect(work.items()).toEqual([{ text: 'Stack: Up running', link: '/stack' }]);

    events.next({ kind: 'exited', exitCode: 0 });
    expect(work.items()).toEqual([]);
  });
});
