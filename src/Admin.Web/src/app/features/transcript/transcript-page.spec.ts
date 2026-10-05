import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { TranscriptView } from '../../core/host/host-types';
import { TRANSCRIPT_POLL_MS, TranscriptPage } from './transcript-page';

const view: TranscriptView = {
  dropped: 0,
  entries: [
    {
      sequence: 1,
      at: '2026-10-04T21:30:00+00:00',
      kind: 'Process',
      command: 'docker compose -f deploy/compose/docker-compose.yml up -d --wait',
      workingDirectory: '/work/blueprint-backend',
      identity: null,
      exitCode: 0,
      status: null,
      settled: true,
    },
    {
      sequence: 2,
      at: '2026-10-04T21:31:00+00:00',
      kind: 'Request',
      command: "curl -i http://localhost:5000/api/v1/orders -H 'Authorization: Bearer <scrubbed>'",
      workingDirectory: null,
      identity: 'demo',
      exitCode: null,
      status: 403,
      settled: true,
    },
    {
      sequence: 3,
      at: '2026-10-04T21:32:00+00:00',
      kind: 'Request',
      command: 'curl -i http://localhost:5000/api/v1/catalog/products',
      workingDirectory: null,
      identity: null,
      exitCode: null,
      status: null,
      settled: true,
    },
  ],
};

function text(el: Element | null): string {
  return el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('TranscriptPage', () => {
  let host: { transcript: ReturnType<typeof vi.fn>; transcriptScript: ReturnType<typeof vi.fn> };
  let writeText: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    // The poll is `timer(0, …)`; fake timers make its first tick deterministic, as the Stack screen's spec says.
    vi.useFakeTimers();
    host = {
      transcript: vi.fn(() => of(view)),
      transcriptScript: vi.fn(() => of('#!/usr/bin/env bash\n')),
    };
    writeText = vi.fn(() => Promise.resolve());
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    TestBed.configureTestingModule({ imports: [TranscriptPage], providers: [{ provide: HostClient, useValue: host }] });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  async function render() {
    const fixture = TestBed.createComponent(TranscriptPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    return fixture;
  }

  it('lists each entry with its outcome, identity, directory and command', async () => {
    const fixture = await render();

    const entries = Array.from(fixture.nativeElement.querySelectorAll('li.entry')) as HTMLElement[];
    expect(entries).toHaveLength(3);
    expect(text(entries[0].querySelector('.outcome'))).toBe('exit 0');
    expect(text(entries[0].querySelector('.directory'))).toBe('in /work/blueprint-backend');
    expect(text(entries[0].querySelector('.command'))).toBe('docker compose -f deploy/compose/docker-compose.yml up -d --wait');
    expect(text(entries[1].querySelector('.outcome'))).toBe('HTTP 403');
    expect(text(entries[1].querySelector('.identity'))).toBe('as demo');
    expect(text(entries[2].querySelector('.outcome'))).toBe('no answer');
    expect(text(entries[2].querySelector('.identity'))).toBe('anonymous');
  });

  it('says a process is running and a request is waiting until each is settled', async () => {
    host.transcript.mockReturnValue(
      of({
        dropped: 0,
        entries: [
          { ...view.entries[0], exitCode: null, settled: false },
          { ...view.entries[1], status: null, settled: false },
        ],
      }),
    );
    const fixture = await render();

    const outcomes = Array.from(fixture.nativeElement.querySelectorAll('li.entry .outcome')) as HTMLElement[];
    expect(outcomes.map((o) => text(o))).toEqual(['running', 'waiting']);
  });

  it('polls the host again on its interval', async () => {
    await render();
    await vi.advanceTimersByTimeAsync(TRANSCRIPT_POLL_MS);

    expect(host.transcript).toHaveBeenCalledTimes(2);
  });

  it("copies the host's script, not one built from the rows", async () => {
    const fixture = await render();

    (fixture.nativeElement.querySelector('button.copy') as HTMLButtonElement).click();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith('#!/usr/bin/env bash\n');
    expect(text(fixture.nativeElement.querySelector('.copied'))).toBe('Copied.');
  });

  it('says why when the copy fails', async () => {
    host.transcriptScript.mockReturnValue(throwError(() => ({ message: 'refused' })));
    const fixture = await render();

    (fixture.nativeElement.querySelector('button.copy') as HTMLButtonElement).click();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(writeText).not.toHaveBeenCalled();
    expect(text(fixture.nativeElement.querySelector('.error'))).toBe('Not copied: refused');
  });

  it('has nothing to copy before anything was done, and counts what was dropped', async () => {
    host.transcript.mockReturnValue(of({ dropped: 4, entries: [] }));
    const fixture = await render();

    expect((fixture.nativeElement.querySelector('button.copy') as HTMLButtonElement).disabled).toBe(true);
    expect(text(fixture.nativeElement.querySelector('.empty'))).toContain('Nothing yet');
    expect(text(fixture.nativeElement.querySelector('.dropped'))).toBe('4 earlier entries were dropped.');
  });

  it('keeps the last transcript and says so when a poll fails, until the next answer', async () => {
    const fixture = await render();
    host.transcript.mockReturnValue(throwError(() => ({ message: 'connection refused' })));

    await vi.advanceTimersByTimeAsync(TRANSCRIPT_POLL_MS);
    fixture.detectChanges();

    expect(text(fixture.nativeElement.querySelector('.error'))).toBe('The host did not answer: connection refused');
    expect(fixture.nativeElement.querySelectorAll('li.entry')).toHaveLength(3);

    host.transcript.mockReturnValue(of(view));
    await vi.advanceTimersByTimeAsync(TRANSCRIPT_POLL_MS);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.error')).toBeNull();
  });
});
