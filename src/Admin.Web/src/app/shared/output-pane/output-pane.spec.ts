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
    expect(pre.querySelector('.stderr')?.textContent).toContain('two');
    expect(fixture.nativeElement.textContent).toContain('exited 3');
  });
});
