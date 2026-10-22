import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { NEVER } from 'rxjs';
import { SseClient } from '../core/host/sse-client';
import { ComposeRun } from '../features/stack/stack-feed';
import { OutputPane } from '../shared/output-pane/output-pane';
import { JobOutputWidget } from './job-output-widget';
import { PageJobs } from './page-jobs';
import { WIDGET_CONTEXT } from './widget-context';

describe('JobOutputWidget', () => {
  let follow: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    follow = vi.fn(() => NEVER);
    TestBed.configureTestingModule({
      providers: [
        { provide: SseClient, useValue: { follow } },
        { provide: WIDGET_CONTEXT, useValue: { instanceId: 'out', pageId: 'p' } },
      ],
    });
  });

  function render(config: Record<string, unknown>) {
    const fixture = TestBed.createComponent(JobOutputWidget);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    const pane = () => fixture.debugElement.query(By.directive(OutputPane))?.componentInstance as OutputPane | undefined;
    return { fixture, pane };
  }

  it('says no job has started yet, then follows the job its page starts or opens', () => {
    const { fixture, pane } = render({ jobId: null });
    expect(fixture.nativeElement.textContent).toContain('No job yet');

    TestBed.inject(PageJobs).show('another-page', 'job-0');
    fixture.detectChanges();
    expect(pane()).toBeUndefined();

    TestBed.inject(PageJobs).show('p', 'job-1');
    fixture.detectChanges();
    expect(pane()?.jobId()).toBe('job-1');
    expect(follow).toHaveBeenCalledWith('job-1');
  });

  it('shows the job its config names, whatever the page starts', () => {
    const { fixture, pane } = render({ jobId: 'job-7' });

    TestBed.inject(PageJobs).show('p', 'job-1');
    fixture.detectChanges();

    expect(pane()?.jobId()).toBe('job-7');
  });

  it('reads a Compose job’s stderr as progress', () => {
    const { fixture, pane } = render({ jobId: 'up-1' });
    expect(pane()?.composeStderr()).toBe(false);

    TestBed.inject(ComposeRun).started('up-1', 'Up');
    fixture.detectChanges();

    expect(pane()?.composeStderr()).toBe(true);
  });
});
