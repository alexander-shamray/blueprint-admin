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
