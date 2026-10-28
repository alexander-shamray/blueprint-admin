import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { JobEvent, SseClient } from '../../core/host/sse-client';
import { OutputPane } from './output-pane';

describe('OutputPane', () => {
  it('renders lines as they arrive and the exit code at the end', async () => {
    const events = new Subject<JobEvent>();
    const sse = { follow: vi.fn(() => events.asObservable()) };
    TestBed.configureTestingModule({ imports: [OutputPane], providers: [{ provide: SseClient, useValue: sse }] });
    const fixture = TestBed.createComponent(OutputPane);
    fixture.componentRef.setInput('jobId', 'j1');
    fixture.detectChanges();

    events.next({ kind: 'line', line: { sequence: 0, at: '', stream: 'Stdout', text: 'one' } });
    events.next({ kind: 'line', line: { sequence: 1, at: '', stream: 'Stderr', text: 'two' } });
    events.next({ kind: 'exited', exitCode: 3 });
    fixture.detectChanges();

    const pre = fixture.nativeElement.querySelector('pre') as HTMLElement;
    expect(sse.follow).toHaveBeenCalledWith('j1');
    expect(pre.textContent).toContain('one');
    expect(pre.querySelector('.stderr')?.textContent?.trim()).toBe('[stderr] two');
    expect(pre.textContent).not.toContain('[stderr] one');
    expect(fixture.nativeElement.textContent).toContain('exited 3');
  });

  it('is a log that is read on demand and never announced line by line, with a status that says only start and end', () => {
    const events = new Subject<JobEvent>();
    const sse = { follow: vi.fn(() => events.asObservable()) };
    TestBed.configureTestingModule({ imports: [OutputPane], providers: [{ provide: SseClient, useValue: sse }] });
    const fixture = TestBed.createComponent(OutputPane);
    fixture.componentRef.setInput('jobId', 'j1');
    fixture.detectChanges();

    const pre = fixture.nativeElement.querySelector('pre') as HTMLElement;
    const status = fixture.nativeElement.querySelector('[role="status"]') as HTMLElement;
    expect(pre.getAttribute('role')).toBe('log');
    expect(pre.getAttribute('aria-live')).toBe('off');
    expect(pre.getAttribute('tabindex')).toBe('0');
    expect(status.textContent).toContain('A job is running');

    events.next({ kind: 'line', line: { sequence: 0, at: '', stream: 'Stdout', text: 'Container sql Started' } });
    fixture.detectChanges();
    expect(status.textContent).not.toContain('Container sql');

    events.next({ kind: 'exited', exitCode: 0 });
    fixture.detectChanges();
    expect(status.textContent?.trim()).toBe('The job exited 0.');
  });

  it('marks every stderr line by default, and for a Compose job only the lines that say something failed', () => {
    const events = new Subject<JobEvent>();
    const sse = { follow: vi.fn(() => events.asObservable()) };
    TestBed.configureTestingModule({ imports: [OutputPane], providers: [{ provide: SseClient, useValue: sse }] });
    const fixture = TestBed.createComponent(OutputPane);
    fixture.componentRef.setInput('jobId', 'j1');
    fixture.detectChanges();
    events.next({ kind: 'line', line: { sequence: 0, at: '', stream: 'Stderr', text: 'Pulling the layers' } });
    events.next({ kind: 'line', line: { sequence: 1, at: '', stream: 'Stderr', text: 'Error response from daemon' } });
    fixture.detectChanges();
    const marked = () => Array.from(fixture.nativeElement.querySelectorAll('pre .stderr')).length;
    expect(marked()).toBe(2);

    fixture.componentRef.setInput('composeStderr', true);
    fixture.detectChanges();

    expect(marked()).toBe(1);
    expect(fixture.nativeElement.querySelector('pre .stderr')?.textContent).toContain('Error response');
  });

  it('shows the stream error and keeps the lines already received', () => {
    const events = new Subject<JobEvent>();
    const sse = { follow: vi.fn(() => events.asObservable()) };
    TestBed.configureTestingModule({ imports: [OutputPane], providers: [{ provide: SseClient, useValue: sse }] });
    const fixture = TestBed.createComponent(OutputPane);
    fixture.componentRef.setInput('jobId', 'j1');
    fixture.detectChanges();

    events.next({ kind: 'line', line: { sequence: 0, at: '', stream: 'Stdout', text: 'one' } });
    events.error(new Error('The host closed the job stream.'));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('The host closed the job stream.');
    expect(fixture.nativeElement.querySelector('pre')?.textContent).toContain('one');
  });

  it('tabulates the images a Compose job pulls, and leaves their lines out of the log until asked', () => {
    const events = new Subject<JobEvent>();
    const sse = { follow: vi.fn(() => events.asObservable()) };
    TestBed.configureTestingModule({ imports: [OutputPane], providers: [{ provide: SseClient, useValue: sse }] });
    const fixture = TestBed.createComponent(OutputPane);
    fixture.componentRef.setInput('jobId', 'up');
    fixture.componentRef.setInput('composeStderr', true);
    fixture.detectChanges();
    const send = (sequence: number, event: object) =>
      events.next({ kind: 'line', line: { sequence, at: '', stream: 'Stderr', text: JSON.stringify(event) } });

    send(0, { id: 'Image grafana/otel-lgtm:0.37.0', status: 'Working', text: 'Pulling' });
    send(1, {
      id: '3c5e70de2899',
      parent_id: 'Image grafana/otel-lgtm:0.37.0',
      status: 'Working',
      text: 'Downloading',
      details: '241.2MB',
      current: 241_200_000,
      total: 900_400_000,
    });
    send(2, { id: 'Image redis:7.4.11-alpine', status: 'Working', text: 'Pulling' });
    send(3, { id: 'Container commerce-sql-1', status: 'Done', text: 'Healthy' });
    fixture.detectChanges();

    const pull = fixture.nativeElement.querySelector('section.pull') as HTMLElement;
    const rows = Array.from(pull.querySelectorAll('tbody tr')) as HTMLElement[];
    expect(pull.querySelector('.pull-summary')?.textContent).toContain('Images left to pull: 2 of 2');
    expect(rows.map((r) => r.querySelector('th')?.textContent)).toEqual([
      'grafana/otel-lgtm:0.37.0',
      'redis:7.4.11-alpine',
    ]);
    expect(rows[0].querySelector('.size')?.textContent).toBe('241 MB / 900 MB');
    expect(rows[0].querySelector('.state')?.textContent).toBe('downloading');
    expect(rows[1].querySelector('.state')?.textContent).toBe('waiting');

    const pre = fixture.nativeElement.querySelector('pre') as HTMLElement;
    expect(pre.textContent).not.toContain('3c5e70de2899');
    expect(pre.textContent).not.toContain('Container commerce-sql-1');
    expect(pre.querySelector('.stderr')).toBeNull();

    (fixture.nativeElement.querySelector('button.pull-lines') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(pre.textContent).toContain(' 3c5e70de2899  Downloading 241.2MB');
    expect(pre.textContent).toContain(' Container commerce-sql-1  Healthy');
  });

  it('marks a line Compose reports as an error, though it is JSON', () => {
    const events = new Subject<JobEvent>();
    const sse = { follow: vi.fn(() => events.asObservable()) };
    TestBed.configureTestingModule({ imports: [OutputPane], providers: [{ provide: SseClient, useValue: sse }] });
    const fixture = TestBed.createComponent(OutputPane);
    fixture.componentRef.setInput('jobId', 'up');
    fixture.componentRef.setInput('composeStderr', true);
    fixture.detectChanges();

    events.next({
      kind: 'line',
      line: { sequence: 0, at: '', stream: 'Stderr', text: '{"id":"Container sql-1","status":"Error","text":"Error"}' },
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('pre .stderr')?.textContent).toContain('Container sql-1');
  });

  it('turns an image’s row failed on the failure line, though that line stays in the log', () => {
    const events = new Subject<JobEvent>();
    const sse = { follow: vi.fn(() => events.asObservable()) };
    TestBed.configureTestingModule({ imports: [OutputPane], providers: [{ provide: SseClient, useValue: sse }] });
    const fixture = TestBed.createComponent(OutputPane);
    fixture.componentRef.setInput('jobId', 'up');
    fixture.componentRef.setInput('composeStderr', true);
    fixture.detectChanges();

    const image = 'Image grafana/otel-lgtm:0.37.0';
    events.next({
      kind: 'line',
      line: { sequence: 0, at: '', stream: 'Stderr', text: JSON.stringify({ id: image, text: 'Pulling' }) },
    });
    events.next({
      kind: 'line',
      line: {
        sequence: 1,
        at: '',
        stream: 'Stderr',
        text: JSON.stringify({ id: image, status: 'Error', text: 'Error' }),
      },
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('section.pull tbody tr .state')?.textContent).toBe('failed');
    expect(fixture.nativeElement.querySelector('pre .stderr')?.textContent).toContain('grafana/otel-lgtm:0.37.0');
  });

  it('fills its parent only when asked, so a tile scrolls its log rather than itself', () => {
    TestBed.configureTestingModule({
      imports: [OutputPane],
      providers: [{ provide: SseClient, useValue: { follow: vi.fn() } }],
    });
    const fixture = TestBed.createComponent(OutputPane);
    fixture.detectChanges();
    expect(fixture.nativeElement.classList).not.toContain('fill');

    fixture.componentRef.setInput('fill', true);
    fixture.detectChanges();
    expect(fixture.nativeElement.classList).toContain('fill');
  });

  it('clears a previous stream error when it follows another job', () => {
    const first = new Subject<JobEvent>();
    const second = new Subject<JobEvent>();
    const sse = { follow: vi.fn((id: string) => (id === 'j1' ? first : second).asObservable()) };
    TestBed.configureTestingModule({ imports: [OutputPane], providers: [{ provide: SseClient, useValue: sse }] });
    const fixture = TestBed.createComponent(OutputPane);
    fixture.componentRef.setInput('jobId', 'j1');
    fixture.detectChanges();
    first.error(new Error('The host closed the job stream.'));
    fixture.detectChanges();

    fixture.componentRef.setInput('jobId', 'j2');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.error')).toBeNull();
  });

  describe('a Compose job’s containers and a multi-step job’s steps', () => {
    const open = (events: Subject<JobEvent>) => {
      const sse = { follow: vi.fn(() => events.asObservable()) };
      TestBed.configureTestingModule({ imports: [OutputPane], providers: [{ provide: SseClient, useValue: sse }] });
      const fixture = TestBed.createComponent(OutputPane);
      fixture.componentRef.setInput('jobId', 'j1');
      fixture.componentRef.setInput('composeStderr', true);
      fixture.detectChanges();
      return fixture;
    };
    const send = (events: Subject<JobEvent>, sequence: number, text: string, stream: 'Stdout' | 'Stderr' = 'Stderr') =>
      events.next({
        kind: 'line',
        line: { sequence, at: new Date(Date.UTC(2026, 9, 28, 10, 0, sequence)).toISOString(), stream, text },
      });

    it('tabulates the containers, settled ones counted, and leaves their lines in the log behind the toggle', () => {
      const events = new Subject<JobEvent>();
      const fixture = open(events);

      send(events, 0, '{"id":"Container commerce-sql-1","status":"Working","text":"Waiting"}');
      send(events, 1, '{"id":"Container commerce-gateway-1","status":"Working","text":"Waiting"}');
      send(events, 4, '{"id":"Container commerce-sql-1","status":"Done","text":"Healthy"}');
      send(events, 5, '{"id":"Network commerce_default","status":"Done","text":"Created"}');
      fixture.detectChanges();

      const section = fixture.nativeElement.querySelector('section.containers') as HTMLElement;
      expect(section.querySelector('.pull-summary')?.textContent).toBe('Containers settled: 1 of 2');
      const rows = Array.from(section.querySelectorAll('tbody tr')) as HTMLElement[];
      expect(rows.map((r) => [r.className, ...Array.from(r.children, (c) => c.textContent?.trim())])).toEqual([
        ['settled', 'commerce-sql-1', 'healthy', '4s'],
        ['working', 'commerce-gateway-1', 'waiting', ''],
      ]);

      const pre = fixture.nativeElement.querySelector('pre') as HTMLElement;
      expect(pre.textContent).toContain(' Network commerce_default  Created');
      expect(pre.textContent).not.toContain('Container commerce-sql-1');

      (fixture.nativeElement.querySelector('button.pull-lines') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(pre.textContent).toContain(' Container commerce-sql-1  Healthy');
    });

    it('turns a container whose line says Error to failed and keeps that line in the log', () => {
      const events = new Subject<JobEvent>();
      const fixture = open(events);

      send(events, 0, '{"id":"Container commerce-sql-1","status":"Error","text":"Error"}');
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.container-table tbody tr')?.className).toBe('failed');
      expect(fixture.nativeElement.querySelector('pre')?.textContent).toContain('Container commerce-sql-1  Error');
    });

    it('keeps the steps it has read when a reconnect names them again', () => {
      const events = new Subject<JobEvent>();
      const fixture = open(events);

      events.next({ kind: 'steps', steps: ['k6', 'run'] });
      send(events, 0, '== k6 (job j-1)', 'Stdout');
      events.next({ kind: 'steps', steps: ['k6', 'run'] });
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.steps li')?.className).toBe('running');
    });

    it('shows no tables for a job that names no container and pulls no image', () => {
      const events = new Subject<JobEvent>();
      const fixture = open(events);

      send(events, 0, 'plain output', 'Stdout');
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.tables')).toBeNull();
      expect(fixture.nativeElement.querySelector('.steps')).toBeNull();
    });

    it('strips the steps the host names, moves them on at their markers and ends the running one with the job', () => {
      const events = new Subject<JobEvent>();
      const fixture = open(events);
      const strip = () =>
        Array.from(fixture.nativeElement.querySelectorAll('.steps li'), (li) =>
          Array.from((li as HTMLElement).children, (c) => c.textContent?.trim()).join(' '),
        );

      events.next({ kind: 'steps', steps: ['down -v', 'up -d --wait', 'readiness'] });
      fixture.detectChanges();
      expect(strip()).toEqual(['down -v pending', 'up -d --wait pending', 'readiness pending']);

      send(events, 0, '$ docker compose down -v', 'Stdout');
      send(events, 3, '$ docker compose --progress json up -d --wait', 'Stdout');
      fixture.detectChanges();
      expect(strip()).toEqual(['down -v done 3s', 'up -d --wait running', 'readiness pending']);

      send(events, 9, 'docker compose up exited with 1; the reset stops here.');
      events.next({ kind: 'exited', exitCode: 1 });
      fixture.detectChanges();
      expect(strip()).toEqual([
        'down -v done 3s',
        'up -d --wait failed 6s docker compose up exited with 1; the reset stops here.',
        'readiness not run',
      ]);
      expect(fixture.nativeElement.querySelector('pre')?.textContent).toContain('$ docker compose down -v');
    });

    it('draws a k6 run as its latest progress line and keeps the lines behind the toggle', () => {
      const events = new Subject<JobEvent>();
      const fixture = open(events);

      send(events, 0, 'running (0m42.0s), 05/10 VUs, 812 complete and 0 interrupted iterations', 'Stdout');
      send(events, 1, 'query     [   1% ] 00/10 VUs  0m00.9s/3m0s  1.00 iters/s', 'Stdout');
      send(events, 4, 'command • [   0% ] waiting    09.1s', 'Stdout');
      send(events, 2, 'running (0m43.0s), 06/10 VUs, 830 complete and 1 interrupted iterations', 'Stdout');
      send(events, 3, 'k6 exit: 0', 'Stdout');
      fixture.detectChanges();

      const el = fixture.nativeElement as HTMLElement;
      expect(el.querySelector('.k6 .pull-summary')?.textContent).toBe(
        'k6 · 0:43 · 6 of 10 users · 830 iterations complete, 1 interrupted',
      );
      const bar = el.querySelector('.k6 progress') as HTMLProgressElement;
      expect([bar.value, bar.max]).toEqual([6, 10]);
      expect(el.querySelectorAll('.k6 progress')).toHaveLength(1);
      expect(el.querySelector('pre')?.textContent).toBe('k6 exit: 0\n');

      (el.querySelector('.pull-lines') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(el.querySelector('pre')?.textContent).toContain('running (0m42.0s)');
    });

    it('draws the length of a one-scenario run and the staircase the host counted, the segment k6 is in marked', () => {
      const events = new Subject<JobEvent>();
      const fixture = open(events);
      fixture.componentRef.setInput('staircase', {
        users: 50,
        maxUsers: 400,
        elapsed: '1m10.0s',
        step: 2,
        holding: true,
      });

      send(events, 0, 'running (1m10.0s), 050/400 VUs, 9 complete and 0 interrupted iterations', 'Stdout');
      send(events, 1, 'journey   [  10% ] 050/400 VUs  1m10.0s/17m30s', 'Stdout');
      fixture.detectChanges();

      const el = fixture.nativeElement as HTMLElement;
      expect(el.querySelector('.k6 .pull-summary')?.textContent).toContain('1:10 of 17:30');
      const bars = Array.from(el.querySelectorAll<HTMLProgressElement>('.k6 progress'), (p) => [p.value, p.max]);
      expect(bars).toEqual([
        [50, 400],
        [70, 1050],
      ]);
      expect(
        Array.from(el.querySelectorAll('.staircase li'), (li) => `${li.className} ${li.textContent?.trim()}`),
      ).toEqual(['done Ramp 1', 'done Hold 1', 'done Ramp 2', 'current Hold 2']);
      expect(el.querySelector('.staircase [aria-current="step"]')?.textContent?.trim()).toBe('Hold 2');
    });

    it('draws a staircase only for a run that has printed a progress line, and ends it when k6 has stopped', () => {
      const events = new Subject<JobEvent>();
      const fixture = open(events);
      fixture.componentRef.setInput('staircase', {
        users: 0,
        maxUsers: 400,
        elapsed: '17m30s',
        step: 1,
        holding: false,
      });
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.staircase')).toBeNull();

      send(events, 0, 'running (17m31.0s), 000/400 VUs, 900 complete and 194 interrupted iterations', 'Stdout');
      fixture.detectChanges();

      expect(
        Array.from(fixture.nativeElement.querySelectorAll('.staircase li'), (li) => (li as HTMLElement).className),
      ).toEqual(['done', 'done']);
    });

    it('lists the surfaces of Reset’s readiness wait, ticks them as the polls drop them, and counts down to the host’s cap', () => {
      const events = new Subject<JobEvent>();
      const fixture = open(events);
      const sendNow = (sequence: number, text: string) =>
        events.next({ kind: 'line', line: { sequence, at: new Date().toISOString(), stream: 'Stdout', text } });
      const list = () =>
        Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.surfaces li'), (li) =>
          li.textContent?.replace(/\s+/g, ' ').trim(),
        );

      sendNow(0, '== readiness: up to 120 s');
      sendNow(1, 'waiting for: gateway, ordering');
      sendNow(2, 'waiting for: ordering');
      fixture.detectChanges();

      expect(list()).toEqual(['gateway answers', 'ordering waiting']);
      expect(fixture.nativeElement.querySelector('.readiness .pull-summary')?.textContent).toMatch(
        /^Surfaces answering: 1 of 2 · [012]:\d\d left of 2:00$/,
      );
      // The polls leave the log; the marker stays in it.
      expect(fixture.nativeElement.querySelector('pre')?.textContent).toBe('== readiness: up to 120 s\n');

      sendNow(3, 'ready: gateway, ordering');
      fixture.detectChanges();

      expect(list()).toEqual(['gateway answers', 'ordering answers']);
      expect(fixture.nativeElement.querySelector('.readiness .pull-summary')?.textContent).toBe(
        'Ready: 2 of 2 surfaces answer',
      );
      expect(fixture.nativeElement.querySelector('pre')?.textContent).toContain('ready: gateway, ordering');
    });

    it('turns the surfaces the cap named failed', () => {
      const events = new Subject<JobEvent>();
      const fixture = open(events);

      send(events, 0, '== readiness: up to 120 s', 'Stdout');
      send(events, 1, 'waiting for: ordering, bff', 'Stdout');
      send(events, 2, 'not ready after 120 s: ordering', 'Stderr');
      fixture.detectChanges();

      const el = fixture.nativeElement as HTMLElement;
      expect(Array.from(el.querySelectorAll('.surfaces li'), (li) => li.className)).toEqual(['failed', 'up']);
      expect(el.querySelector('.readiness .pull-summary')?.textContent).toBe('Not ready: 1 of 2 surfaces answer');
    });
  });
});
