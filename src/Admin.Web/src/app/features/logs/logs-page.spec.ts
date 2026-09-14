import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { JobSummary } from '../../core/host/host-types';
import { JobEvent, SseClient } from '../../core/host/sse-client';
import { LogsPage } from './logs-page';

describe('LogsPage', () => {
  let events: Subject<JobEvent>;
  let streams: Record<string, Subject<JobEvent>>;
  let sseFollow: ReturnType<typeof vi.fn>;
  let host: { followLogs: ReturnType<typeof vi.fn> };

  function summary(id: string): JobSummary {
    return { id, commandLine: 'docker compose logs -f', state: 'Running', exitCode: null, startedAt: '' };
  }

  function line(sequence: number, text: string): JobEvent {
    return { kind: 'line', line: { sequence, at: '', stream: 'Stdout', text } };
  }

  beforeEach(() => {
    events = new Subject<JobEvent>();
    // Keyed by job id so a test can give distinct SSE streams to distinct POST responses;
    // existing tests only ever see the default 'logs-1' job, which maps to `events`.
    streams = { 'logs-1': events };
    sseFollow = vi.fn((jobId: string) => (streams[jobId] ??= new Subject<JobEvent>()).asObservable());
    host = { followLogs: vi.fn(() => of(summary('logs-1'))) };
    TestBed.configureTestingModule({
      imports: [LogsPage],
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: sseFollow } },
      ],
    });
  });

  it('follows the selected services and shows lines', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.selected.set(['gateway']);
    fixture.componentInstance.follow();
    events.next(line(0, 'gateway | started'));
    fixture.detectChanges();

    expect(host.followLogs).toHaveBeenCalledWith(['gateway']);
    expect(fixture.nativeElement.textContent).toContain('gateway | started');
  });

  it('filters by text and highlights the correlation id', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    events.next(line(0, 'gateway | proxying CorrelationId=abc'));
    events.next(line(1, 'catalog-api | listed products'));
    fixture.componentInstance.filter.set('gateway');
    fixture.componentInstance.correlationId.set('abc');
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('.line')) as HTMLElement[];
    expect(rows.length).toBe(1);
    expect(rows[0].classList.contains('hit')).toBe(true);
  });

  it('shows the problem detail when the follow request fails and does not go live', () => {
    host.followLogs.mockReturnValueOnce(
      throwError(() => ({ error: { title: 'Backend unreachable', detail: 'Docker did not answer.' } })),
    );

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Docker did not answer.');
    expect(fixture.nativeElement.querySelector('.live')).toBeNull();
    expect(fixture.componentInstance.following()).toBe(false);
  });

  it('shows the stream error, leaves live mode and keeps the lines already received', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    events.next(line(0, 'gateway | started'));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.live')).not.toBeNull();

    events.error(new Error('The host closed the job stream.'));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('The host closed the job stream.');
    expect(fixture.nativeElement.querySelector('.live')).toBeNull();
    expect(fixture.componentInstance.lines().map((l) => l.text)).toEqual(['gateway | started']);
  });

  it('ignores a follow response that arrives after Stop', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.stop();
    sseFollow.mockClear();

    post.next(summary('late-1'));
    fixture.detectChanges();

    expect(sseFollow).not.toHaveBeenCalled();
    expect(fixture.componentInstance.following()).toBe(false);
    expect(fixture.nativeElement.querySelector('.live')).toBeNull();
  });

  it('a second Follow replaces the first even if the first answers late', () => {
    const postA = new Subject<JobSummary>();
    const postB = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(postA.asObservable()).mockReturnValueOnce(postB.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.follow();

    postB.next(summary('job-b'));
    streams['job-b'].next(line(0, 'job-b line'));
    postA.next(summary('job-a'));
    fixture.detectChanges();

    expect(sseFollow.mock.calls.map((c) => c[0])).toEqual(['job-b']);
    expect(fixture.componentInstance.lines().map((l) => l.text)).toEqual(['job-b line']);
  });

  it('enables Stop while the follow request is in flight, and clicking it cancels the request', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.detectChanges();
    const stopButton = fixture.nativeElement.querySelector('button.stop') as HTMLButtonElement;
    expect(stopButton.disabled).toBe(true);

    (fixture.nativeElement.querySelector('button.follow') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(post.observed).toBe(true);
    expect(stopButton.disabled).toBe(false);
    expect(fixture.nativeElement.querySelector('.live')).toBeNull();

    stopButton.click();
    fixture.detectChanges();

    expect(post.observed).toBe(false);
    expect(stopButton.disabled).toBe(true);
    post.next(summary('late-3'));
    fixture.detectChanges();
    expect(sseFollow).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.live')).toBeNull();
  });

  it('destroying the page while a follow is in flight opens no stream', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.destroy();
    sseFollow.mockClear();

    post.next(summary('late-2'));

    expect(sseFollow).not.toHaveBeenCalled();
  });
});
