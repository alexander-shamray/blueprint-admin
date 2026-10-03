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
  let host: { followLogs: ReturnType<typeof vi.fn>; stopFollow: ReturnType<typeof vi.fn> };

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
    host = { followLogs: vi.fn(() => of(summary('logs-1'))), stopFollow: vi.fn(() => of(undefined)) };
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

  it('marks correlation matches and stderr lines with text, not only colour', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    events.next(line(0, 'gateway | proxying CorrelationId=abc'));
    events.next({ kind: 'line', line: { sequence: 1, at: '', stream: 'Stderr', text: 'catalog-api | failed' } });
    events.next(line(2, 'catalog-api | listed products'));
    fixture.componentInstance.correlationId.set('abc');
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('.line')) as HTMLElement[];
    expect(rows.map((r) => r.textContent?.trim())).toEqual([
      '[match] gateway | proxying CorrelationId=abc',
      '[stderr] catalog-api | failed',
      'catalog-api | listed products',
    ]);
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

  it('a Stop before the follow answers opens no stream and stops the job the answer names', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.stop();
    sseFollow.mockClear();

    post.next(summary('late-1'));
    fixture.detectChanges();

    expect(sseFollow).not.toHaveBeenCalled();
    expect(host.stopFollow).toHaveBeenCalledWith('late-1');
    expect(fixture.componentInstance.following()).toBe(false);
    expect(fixture.nativeElement.querySelector('.live')).toBeNull();
  });

  it('a Follow while a request is out sends nothing, and the first answer is followed', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.detectChanges();
    const followButton = fixture.nativeElement.querySelector('button.follow') as HTMLButtonElement;
    followButton.click();
    fixture.detectChanges();

    expect(followButton.disabled).toBe(true);
    fixture.componentInstance.follow();
    expect(host.followLogs).toHaveBeenCalledTimes(1);

    post.next(summary('job-a'));
    fixture.detectChanges();

    expect(sseFollow.mock.calls.map((c) => c[0])).toEqual(['job-a']);
    expect(followButton.disabled).toBe(false);
  });

  it('enables Stop while the follow request is in flight, and clicking it stops the job the request starts', () => {
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

    // Left to answer: a cancelled request cannot stop a job the host has already started.
    expect(post.observed).toBe(true);
    expect(stopButton.disabled).toBe(true);
    post.next(summary('late-3'));
    fixture.detectChanges();
    expect(sseFollow).not.toHaveBeenCalled();
    expect(host.stopFollow).toHaveBeenCalledWith('late-3');
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
    expect(host.stopFollow).toHaveBeenCalledWith('late-2');
  });

  it('Stop ends the host job it is following and closes the stream', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    events.next(line(0, 'gateway | started'));

    fixture.componentInstance.stop();

    expect(host.stopFollow).toHaveBeenCalledWith('logs-1');
    expect(events.observed).toBe(false);
  });

  it('leaving the screen ends the host job it is following', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();

    fixture.destroy();

    expect(host.stopFollow).toHaveBeenCalledWith('logs-1');
  });

  it('a new Follow stops the previous job by name before asking for the next', () => {
    const postB = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(of(summary('job-a'))).mockReturnValueOnce(postB.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.follow();

    // Before B answers, and whether or not it ever does: a B that fails leaves no A behind.
    expect(host.stopFollow).toHaveBeenCalledWith('job-a');
    postB.error(new Error('refused'));
    expect(host.stopFollow).toHaveBeenCalledTimes(1);
  });

  it('leaving the screen after a Stop in flight still stops the job the late answer names', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.stop();
    fixture.destroy();

    expect(post.observed).toBe(true);
    post.next(summary('late-4'));

    expect(host.stopFollow).toHaveBeenCalledWith('late-4');
    expect(sseFollow).not.toHaveBeenCalled();
  });

  it('a stream that fails ends the host job it was reading, and keeps its own error', () => {
    host.stopFollow.mockReturnValueOnce(throwError(() => new Error('host gone')));
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();

    events.error(new Error('stream dropped'));

    expect(host.stopFollow).toHaveBeenCalledWith('logs-1');
    expect(fixture.componentInstance.error()).toBe('stream dropped');
    expect(fixture.componentInstance.following()).toBe(false);
  });

  it('a stop that fails keeps the job, so Stop stays enabled and tries it again', () => {
    host.stopFollow.mockReturnValueOnce(throwError(() => new Error('host gone')));
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.stop();
    fixture.detectChanges();
    const stopButton = fixture.nativeElement.querySelector('button.stop') as HTMLButtonElement;

    expect(stopButton.disabled).toBe(false);
    expect(fixture.componentInstance.error()).toBe('host gone');

    stopButton.click();
    fixture.detectChanges();

    expect(host.stopFollow.mock.calls.map((c) => c[0])).toEqual(['logs-1', 'logs-1']);
    expect(stopButton.disabled).toBe(true);
  });

  it('a stop still out is not sent again, whoever asks', () => {
    // An HTTP stop answers later than of(): the stop-on-arrival path, its completion, Stop and
    // teardown would each send one while the first is out.
    const post = new Subject<JobSummary>();
    const stopping = new Subject<void>();
    host.followLogs.mockReturnValueOnce(post.asObservable());
    host.stopFollow.mockReturnValueOnce(stopping.asObservable());
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.stop();

    post.next(summary('late-5'));
    fixture.componentInstance.stop();
    fixture.destroy();

    expect(host.stopFollow).toHaveBeenCalledTimes(1);
    stopping.complete();
    expect(fixture.componentInstance.hostJob()).toBeNull();
  });

  it('a stop that fails on a late answer is sent once, and the job stays for Stop', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());
    host.stopFollow.mockReturnValueOnce(throwError(() => new Error('host gone')));
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.stop();

    post.next(summary('late-6'));

    expect(host.stopFollow).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.hostJob()).toBe('late-6');
  });

  it('leaving the screen after a failed stop makes one last attempt, and no more', () => {
    host.stopFollow
      .mockReturnValueOnce(throwError(() => new Error('host gone')))
      .mockReturnValueOnce(throwError(() => new Error('still gone')));
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.stop();

    fixture.destroy();

    expect(host.stopFollow.mock.calls.map((c) => c[0])).toEqual(['logs-1', 'logs-1']);
  });

  it('a late failure to stop the previous job is not shown against the next follow', () => {
    const stopA = new Subject<void>();
    host.stopFollow.mockReturnValueOnce(stopA.asObservable());
    host.followLogs.mockReturnValueOnce(of(summary('job-a'))).mockReturnValueOnce(of(summary('job-b')));
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.follow();

    stopA.error(new Error('host gone'));

    expect(fixture.componentInstance.error()).toBeNull();
    expect(fixture.componentInstance.hostJob()).toBe('job-b');
  });

  it('a slow stop of the previous job does not forget the next one', () => {
    const stopA = new Subject<void>();
    host.stopFollow.mockReturnValueOnce(stopA.asObservable());
    host.followLogs.mockReturnValueOnce(of(summary('job-a'))).mockReturnValueOnce(of(summary('job-b')));
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.follow();

    stopA.complete();

    expect(fixture.componentInstance.hostJob()).toBe('job-b');
  });

  it('a job that has exited is not stopped again', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    events.next({ kind: 'exited', exitCode: 0 });

    fixture.componentInstance.stop();

    expect(host.stopFollow).not.toHaveBeenCalled();
  });
});
