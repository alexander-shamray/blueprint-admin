import { TestBed } from '@angular/core/testing';
import { JobEvent, SseClient } from './sse-client';

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  readonly listeners = new Map<string, (e: MessageEvent) => void>();
  closed = false;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: (e: MessageEvent) => void): void {
    this.listeners.set(type, listener);
  }

  close(): void {
    this.closed = true;
  }

  emit(type: string, data: unknown, lastEventId = ''): void {
    this.listeners.get(type)?.(new MessageEvent(type, { data: JSON.stringify(data), lastEventId }));
  }
}

describe('SseClient', () => {
  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('opens the job stream and maps line and exited events', () => {
    const client = TestBed.inject(SseClient);
    const seen: JobEvent[] = [];
    let completed = false;

    client.follow('j1', 4).subscribe({ next: (e) => seen.push(e), complete: () => (completed = true) });

    const source = FakeEventSource.instances[0];
    expect(source.url).toBe('/api/jobs/j1/stream?after=4');
    source.emit('line', { sequence: 5, at: '', stream: 'Stdout', text: 'hello' }, '5');
    source.emit('exited', { exitCode: 0 });

    expect(seen).toEqual([
      { kind: 'line', line: { sequence: 5, at: '', stream: 'Stdout', text: 'hello' } },
      { kind: 'exited', exitCode: 0 },
    ]);
    expect(completed).toBe(true);
    expect(source.closed).toBe(true);
  });

  it('opens a plain follow with no query, so a reconnect resumes from Last-Event-ID', () => {
    const client = TestBed.inject(SseClient);

    client.follow('j1').subscribe();

    expect(FakeEventSource.instances[0].url).toBe('/api/jobs/j1/stream');
  });

  it('closes the source on unsubscribe', () => {
    const client = TestBed.inject(SseClient);

    const sub = client.follow('j2').subscribe();
    sub.unsubscribe();

    expect(FakeEventSource.instances[0].closed).toBe(true);
  });
});
