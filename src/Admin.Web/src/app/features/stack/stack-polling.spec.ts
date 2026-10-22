import { TestBed } from '@angular/core/testing';
import { NEVER, of } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { SseClient } from '../../core/host/sse-client';
import { builtInWidgets } from '../../workspace/built-in-pages';
import { WIDGETS } from '../../workspace/widgets';
import { WorkspaceGrid } from '../../workspace/workspace-grid';

const stack = {
  backend: { reachable: true, error: null, services: [] },
  frontend: { job: null, installed: true, deviceJob: null },
  reachability: [],
  lease: null,
  loadRun: null,
};

function fakeHost() {
  return {
    stack: vi.fn(() => of(stack)),
    telemetryHealth: vi.fn(() => of({ reachable: true, error: null, services: [] })),
    telemetryAlerts: vi.fn(() => of({ reachable: true, error: null, loaded: [], waiting: [] })),
    config: vi.fn(() => of(undefined)),
    doctor: vi.fn(() => of({ checks: [] })),
    frontendDeviceReads: vi.fn(() => of({ checks: [] })),
  };
}

// Issue #385: a page that is not showing polls nothing. Leaving a page destroys the page and the grid that holds its
// widgets, which is what this does to the built-in Stack page as the console ships it.
describe('the Stack page', () => {
  let host: ReturnType<typeof fakeHost>;

  beforeEach(async () => {
    // The widgets' modules, loaded before the clock is faked, so the grid's lazy import lands on a microtask.
    await Promise.all(WIDGETS.map((w) => w.component()));
    vi.useFakeTimers();
    host = fakeHost();
    TestBed.configureTestingModule({
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => NEVER } },
      ],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('polls the stack, the signals and the alerts while it shows, and nothing once it is left', async () => {
    const page = TestBed.createComponent(WorkspaceGrid);
    page.componentRef.setInput('pageId', 'stack');
    page.componentRef.setInput('widgets', builtInWidgets('stack'));
    page.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    page.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    expect(page.nativeElement.querySelectorAll('.ws-body > *').length).toBe(9);

    const reads = () => [host.stack, host.telemetryHealth, host.telemetryAlerts].map((read) => read.mock.calls.length);
    // rxjs schedules the first tick of `timer(0, …)` as an interval, which the fake clock fires a millisecond in.
    await vi.advanceTimersByTimeAsync(1);
    const shown = reads();
    expect(shown).toEqual([1, 1, 1]);

    // Past the ticks at 3, 6, 9, 12 and 15 s and short of the next, so no tick sits on the window's edge.
    await vi.advanceTimersByTimeAsync(16_500);
    // Five widgets read the stack and share one poll, a read each 3 s; the strips read each 15 s.
    const before = reads();
    expect(before).toEqual([shown[0] + 5, shown[1] + 1, shown[2] + 1]);

    page.destroy();
    await vi.advanceTimersByTimeAsync(120_000);

    expect(reads()).toEqual(before);
  });
});
