import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { JobEvent, SseClient } from '../../../core/host/sse-client';
import { PageJobs } from '../../../workspace/page-jobs';
import { WIDGET_CONTEXT } from '../../../workspace/widget-context';
import { StackControlsWidget } from './stack-controls-widget';

const stack = {
  backend: { reachable: true, error: null, services: [] },
  frontend: { job: null, installed: true, deviceJob: null },
  reachability: [],
  lease: null,
  loadRun: null,
};

describe('StackControlsWidget', () => {
  let events: Subject<JobEvent>;
  let host: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    vi.useFakeTimers();
    events = new Subject<JobEvent>();
    host = {
      stack: vi.fn(() => of(stack)),
      config: vi.fn(() => of(undefined)),
      backendUp: vi.fn(() =>
        of({ id: 'up-1', commandLine: 'docker compose up', state: 'Running', exitCode: null, startedAt: '' }),
      ),
      backendDown: vi.fn(() =>
        of({ id: 'down-1', commandLine: 'docker compose down', state: 'Running', exitCode: null, startedAt: '' }),
      ),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => events.asObservable() } },
        // As the grid tells a widget on page `p`; leaving the page destroys it, and coming back creates it again.
        { provide: WIDGET_CONTEXT, useValue: { instanceId: 'controls', pageId: 'p' } },
      ],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  async function render() {
    const fixture = TestBed.createComponent(StackControlsWidget);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    const q = <T extends HTMLElement>(selector: string) => fixture.nativeElement.querySelector(selector) as T;
    return { fixture, q };
  }

  it('starts the down -v confirmation empty when the widget is created again', async () => {
    const first = await render();
    const confirm = first.q<HTMLInputElement>('input[placeholder="type: down -v"]');
    confirm.value = 'down -v';
    confirm.dispatchEvent(new Event('input'));
    first.fixture.detectChanges();
    expect(first.q<HTMLButtonElement>('button.wipe').disabled).toBe(false);

    first.fixture.destroy();
    const again = await render();

    expect(again.q<HTMLInputElement>('input[placeholder="type: down -v"]').value).toBe('');
    expect(again.fixture.componentInstance.confirmText()).toBe('');
    expect(again.q<HTMLButtonElement>('button.wipe').disabled).toBe(true);
  });

  it('makes a job it starts the job its page shows', async () => {
    const { fixture, q } = await render();

    q<HTMLButtonElement>('button.up').click();
    fixture.detectChanges();

    expect(TestBed.inject(PageJobs).of('p')()).toBe('up-1');
    expect(TestBed.inject(PageJobs).of('another-page')()).toBeNull();
  });

  it('still holds the buttons for an Up started before the page was left, and frees them when it ends', async () => {
    const first = await render();
    first.q<HTMLButtonElement>('button.up').click();
    first.fixture.destroy();

    const again = await render();
    expect(again.q<HTMLButtonElement>('button.down').disabled).toBe(true);
    expect(again.q('button.up').textContent?.trim()).toMatch(/^Up… /);

    events.next({ kind: 'exited', exitCode: 0 });
    again.fixture.detectChanges();
    expect(again.q<HTMLButtonElement>('button.down').disabled).toBe(false);
    expect(again.q('.job-outcome').textContent?.trim()).toBe('Up finished: exit 0 in 0:00');
  });
});
