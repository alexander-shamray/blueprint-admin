import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { JobSummary } from '../../core/host/host-types';
import { JobEvent, SseClient } from '../../core/host/sse-client';
import { RunningWork } from '../../workspace/running-work';
import { LogFollowService, MAX_LINES, lineService } from './log-follow';

describe('LogFollowService', () => {
  let events: Subject<JobEvent>;
  let streams: Record<string, Subject<JobEvent>>;
  let sseFollow: ReturnType<typeof vi.fn>;
  let host: { followLogs: ReturnType<typeof vi.fn>; stopFollow: ReturnType<typeof vi.fn> };
  let service: LogFollowService;

  function summary(id: string): JobSummary {
    return { id, commandLine: 'docker compose logs -f', state: 'Running', exitCode: null, startedAt: '' };
  }

  function line(sequence: number, text: string): JobEvent {
    return { kind: 'line', line: { sequence, at: '', stream: 'Stdout', text } };
  }

  beforeEach(() => {
    events = new Subject<JobEvent>();
    // Keyed by job id so a test can give distinct SSE streams to distinct POST responses.
    streams = { 'logs-1': events };
    sseFollow = vi.fn((jobId: string) => (streams[jobId] ??= new Subject<JobEvent>()).asObservable());
    host = { followLogs: vi.fn(() => of(summary('logs-1'))), stopFollow: vi.fn(() => of(undefined)) };
    TestBed.configureTestingModule({
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: sseFollow } },
      ],
    });
    service = TestBed.inject(LogFollowService);
  });

  it('names the Compose service a line came from, without its replica suffix', () => {
    expect(lineService('catalog-api-1  | started')).toBe('catalog-api');
    expect(lineService('gateway       | started')).toBe('gateway');
    expect(lineService('no prefix at all')).toBeNull();
  });

  it('follows every service when no widget has asked, and shows lines', () => {
    service.follow();
    events.next(line(0, 'gateway | started'));

    expect(host.followLogs).toHaveBeenCalledWith([]);
    expect(service.lines().map((l) => l.text)).toEqual(['gateway | started']);
    expect(service.following()).toBe(true);
  });

  it('follows the union of what the widgets ask for, and every service when one asks for every one', () => {
    service.ask('a', ['gateway']);
    service.ask('b', ['catalog-api', 'gateway']);
    expect(service.services()).toEqual(['catalog-api', 'gateway']);

    service.ask('c', []);
    expect(service.services()).toEqual([]);

    service.ask('c', ['web-bff']);
    service.follow();
    expect(host.followLogs).toHaveBeenCalledWith(['catalog-api', 'gateway', 'web-bff']);
  });

  it('keeps a bounded buffer, dropping the oldest', () => {
    service.follow();
    for (let i = 0; i < MAX_LINES + 3; i++) events.next(line(i, `gateway | ${i}`));

    expect(service.lines().length).toBe(MAX_LINES);
    expect(service.lines()[0].text).toBe('gateway | 3');
  });

  it('restarts the follow when the union changes while it runs, stopping the old job by name', () => {
    host.followLogs.mockReturnValueOnce(of(summary('job-a'))).mockReturnValueOnce(of(summary('job-b')));
    service.ask('a', ['gateway']);
    service.follow();
    expect(host.followLogs).toHaveBeenLastCalledWith(['gateway']);
    streams['job-a'].next(line(0, 'gateway | before'));

    service.ask('b', ['web-bff']);

    expect(host.stopFollow).toHaveBeenCalledWith('job-a');
    expect(host.followLogs).toHaveBeenLastCalledWith(['gateway', 'web-bff']);
    expect(service.hostJob()).toBe('job-b');
    // The lines already received are kept across a restart (§4.4); only a Follow starts them afresh.
    expect(service.lines().map((l) => l.text)).toEqual(['gateway | before']);
    service.stop();
    service.follow();
    expect(service.lines()).toEqual([]);
  });

  it('a change undone before the request answers restarts nothing', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());
    service.ask('a', ['gateway']);
    service.follow();

    service.ask('b', ['web-bff']);
    service.withdraw('b');
    post.next(summary('job-a'));

    expect(host.followLogs).toHaveBeenCalledTimes(1);
    expect(sseFollow).toHaveBeenCalledWith('job-a');
    expect(service.following()).toBe(true);
  });

  it('does not restart for a change that leaves the union as it was, or when nothing follows', () => {
    service.ask('a', ['gateway']);
    service.ask('b', ['gateway']);
    expect(host.followLogs).not.toHaveBeenCalled();

    service.follow();
    host.followLogs.mockClear();
    service.ask('b', ['gateway']);
    service.ask('c', ['gateway']);
    expect(host.followLogs).not.toHaveBeenCalled();
  });

  it('a change while the request is out restarts once the answer names its job', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable()).mockReturnValueOnce(of(summary('job-b')));
    service.ask('a', ['gateway']);
    service.follow();

    service.ask('b', ['web-bff']);
    expect(host.followLogs).toHaveBeenCalledTimes(1);

    post.next(summary('job-a'));
    post.complete();

    expect(sseFollow).not.toHaveBeenCalledWith('job-a');
    expect(host.stopFollow).toHaveBeenCalledWith('job-a');
    expect(host.followLogs).toHaveBeenCalledTimes(2);
    expect(host.followLogs).toHaveBeenLastCalledWith(['gateway', 'web-bff']);
  });

  it('a widget going away leaves the follow running, with the services it had', () => {
    service.ask('a', ['gateway']);
    service.follow();

    service.withdraw('a');

    expect(host.stopFollow).not.toHaveBeenCalled();
    expect(host.followLogs).toHaveBeenCalledTimes(1);
    expect(service.following()).toBe(true);
    expect(service.hostJob()).toBe('logs-1');
  });

  it('is listed as running work while it follows, and not after Stop', () => {
    const items = () => TestBed.inject(RunningWork).items();
    expect(items()).toEqual([]);

    service.follow();
    expect(items()).toEqual([{ text: 'Logs: following', link: '/logs' }]);

    service.stop();
    expect(items()).toEqual([]);
  });

  it('shows the problem detail when the follow request fails and does not go live', () => {
    host.followLogs.mockReturnValueOnce(
      throwError(() => ({ error: { title: 'Backend unreachable', detail: 'Docker did not answer.' } })),
    );

    service.follow();

    expect(service.error()).toBe('Docker did not answer.');
    expect(service.following()).toBe(false);
  });

  it('shows the stream error, leaves live mode and keeps the lines already received', () => {
    service.follow();
    events.next(line(0, 'gateway | started'));

    events.error(new Error('The host closed the job stream.'));

    expect(service.error()).toBe('The host closed the job stream.');
    expect(service.following()).toBe(false);
    expect(service.lines().map((l) => l.text)).toEqual(['gateway | started']);
  });

  it('a Stop before the follow answers opens no stream and stops the job the answer names', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    service.follow();
    service.stop();
    sseFollow.mockClear();
    post.next(summary('late-1'));

    expect(sseFollow).not.toHaveBeenCalled();
    expect(host.stopFollow).toHaveBeenCalledWith('late-1');
    expect(service.following()).toBe(false);
  });

  it('a Follow while a request is out sends nothing, and the first answer is followed', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    service.follow();
    service.follow();
    expect(host.followLogs).toHaveBeenCalledTimes(1);
    expect(service.requestOut()).toBe(true);

    post.next(summary('job-a'));

    expect(sseFollow.mock.calls.map((c) => c[0])).toEqual(['job-a']);
    expect(service.requestOut()).toBe(false);
  });

  it('Stop ends the host job it is following and closes the stream', () => {
    service.follow();
    events.next(line(0, 'gateway | started'));

    service.stop();

    expect(host.stopFollow).toHaveBeenCalledWith('logs-1');
    expect(events.observed).toBe(false);
  });

  it('a new Follow stops the previous job by name before asking for the next', () => {
    const postB = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(of(summary('job-a'))).mockReturnValueOnce(postB.asObservable());

    service.follow();
    service.follow();

    // Before B answers, and whether or not it ever does: a B that fails leaves no A behind.
    expect(host.stopFollow).toHaveBeenCalledWith('job-a');
    postB.error(new Error('refused'));
    expect(host.stopFollow).toHaveBeenCalledTimes(1);
  });

  it('a stream that fails ends the host job it was reading, and keeps its own error', () => {
    host.stopFollow.mockReturnValueOnce(throwError(() => new Error('host gone')));
    service.follow();

    events.error(new Error('stream dropped'));

    expect(host.stopFollow).toHaveBeenCalledWith('logs-1');
    expect(service.error()).toBe('stream dropped');
    expect(service.following()).toBe(false);
  });

  it('a stop that fails keeps the job, so Stop can try it again', () => {
    host.stopFollow.mockReturnValueOnce(throwError(() => new Error('host gone')));
    service.follow();
    service.stop();

    expect(service.hostJob()).toBe('logs-1');
    expect(service.error()).toBe('host gone');

    service.stop();

    expect(host.stopFollow.mock.calls.map((c) => c[0])).toEqual(['logs-1', 'logs-1']);
    expect(service.hostJob()).toBeNull();
  });

  it('a stop still out is not sent again, whoever asks', () => {
    // An HTTP stop answers later than of(): the stop-on-arrival path, its completion and Stop would each
    // send one while the first is out.
    const post = new Subject<JobSummary>();
    const stopping = new Subject<void>();
    host.followLogs.mockReturnValueOnce(post.asObservable());
    host.stopFollow.mockReturnValueOnce(stopping.asObservable());
    service.follow();
    service.stop();

    post.next(summary('late-5'));
    service.stop();

    expect(host.stopFollow).toHaveBeenCalledTimes(1);
    stopping.complete();
    expect(service.hostJob()).toBeNull();
  });

  it('a stop that fails on a late answer is sent once, and the job stays for Stop', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());
    host.stopFollow.mockReturnValueOnce(throwError(() => new Error('host gone')));
    service.follow();
    service.stop();

    post.next(summary('late-6'));

    expect(host.stopFollow).toHaveBeenCalledTimes(1);
    expect(service.hostJob()).toBe('late-6');
  });

  it('a late failure to stop the previous job is not shown against the next follow', () => {
    const stopA = new Subject<void>();
    host.stopFollow.mockReturnValueOnce(stopA.asObservable());
    host.followLogs.mockReturnValueOnce(of(summary('job-a'))).mockReturnValueOnce(of(summary('job-b')));
    service.follow();
    service.follow();

    stopA.error(new Error('host gone'));

    expect(service.error()).toBeNull();
    expect(service.hostJob()).toBe('job-b');
  });

  it('a slow stop of the previous job does not forget the next one', () => {
    const stopA = new Subject<void>();
    host.stopFollow.mockReturnValueOnce(stopA.asObservable());
    host.followLogs.mockReturnValueOnce(of(summary('job-a'))).mockReturnValueOnce(of(summary('job-b')));
    service.follow();
    service.follow();

    stopA.complete();

    expect(service.hostJob()).toBe('job-b');
  });

  it('a job that has exited is not stopped again', () => {
    service.follow();
    events.next({ kind: 'exited', exitCode: 0 });

    service.stop();

    expect(host.stopFollow).not.toHaveBeenCalled();
  });

  it('clears the shared buffer', () => {
    service.follow();
    events.next(line(0, 'gateway | started'));

    service.clear();

    expect(service.lines()).toEqual([]);
  });
});
