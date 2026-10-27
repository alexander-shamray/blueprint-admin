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
    events.next({ kind: 'line', line: { sequence: 0, at: '', stream: 'Stderr', text: ' Container sql  Healthy' } });
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
    expect(pre.textContent).toContain(' Container commerce-sql-1  Healthy');
    expect(pre.textContent).not.toContain('3c5e70de2899');
    expect(pre.querySelector('.stderr')).toBeNull();

    (pull.querySelector('button.pull-lines') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(pre.textContent).toContain(' 3c5e70de2899  Downloading 241.2MB');
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
});
