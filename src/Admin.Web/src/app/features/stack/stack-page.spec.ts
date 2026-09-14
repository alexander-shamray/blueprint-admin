import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { SseClient } from '../../core/host/sse-client';
import { StackPage } from './stack-page';

const stack = {
  backend: {
    reachable: true,
    error: null,
    services: [
      { service: 'gateway', state: 'running', health: 'healthy', exitCode: 0, publishedPorts: [5000] },
      { service: 'catalog-migrator', state: 'exited', health: null, exitCode: 0, publishedPorts: [] },
    ],
  },
  reachability: [{ name: 'gateway', url: 'http://localhost:5000/health/ready', up: true, status: 200 }],
};

const config = {
  backendDir: '', frontendDir: '', composeFile: '', fakePlatform: true,
  urls: { gateway: 'http://localhost:5000', catalog: '', ordering: '', bff: '', keycloak: 'http://localhost:8080', grafana: 'http://localhost:3000', client: 'http://localhost:5173' },
};

describe('StackPage', () => {
  let host: { stack: ReturnType<typeof vi.fn>; config: ReturnType<typeof vi.fn>; backendUp: ReturnType<typeof vi.fn>; backendDown: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    // The page polls /api/stack via `timer(0, 3000)`, a real macrotask even at a 0ms delay.
    // Under zoneless TestBed, `fixture.whenStable()` does not wait for that timer to fire
    // (there is nothing to make it "pending" for stability purposes), so a live interval
    // leaves either a flaky first read or a dangling timer after the test ends. Fake timers
    // make the poll deterministic: advance past the first tick, then flush microtasks so the
    // resulting HTTP observable (a synchronous `of(...)` here) reaches the signal.
    vi.useFakeTimers();
    host = {
      stack: vi.fn(() => of(stack)),
      config: vi.fn(() => of(config)),
      backendUp: vi.fn(() => of({ id: 'up-1', commandLine: 'docker compose up', state: 'Running', exitCode: null, startedAt: '' })),
      backendDown: vi.fn(() => of({ id: 'down-1', commandLine: 'docker compose down', state: 'Running', exitCode: null, startedAt: '' })),
    };
    TestBed.configureTestingModule({
      imports: [StackPage],
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => of() } },
      ],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('lists services with state, health and ports, and the reachability strip', async () => {
    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('tbody tr')) as HTMLElement[];
    expect(rows.map((r) => r.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'gateway running healthy 5000',
      'catalog-migrator exited — —',
    ]);
    expect(fixture.nativeElement.querySelector('.reachability')?.textContent).toContain('gateway');
  });

  it('starts an up job and hands its id to the output pane', async () => {
    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);

    (fixture.nativeElement.querySelector('button.up') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(host.backendUp).toHaveBeenCalled();
    expect(fixture.componentInstance.jobId()).toBe('up-1');
  });

  it('keeps the wipe button disabled until the confirmation is typed', async () => {
    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    const wipe = fixture.nativeElement.querySelector('button.wipe') as HTMLButtonElement;
    expect(wipe.disabled).toBe(true);

    fixture.componentInstance.confirmText.set('down -v');
    fixture.detectChanges();
    expect(wipe.disabled).toBe(false);

    wipe.click();
    expect(host.backendDown).toHaveBeenCalledWith(true, 'down -v');
  });

  it('keeps polling after a failed request', async () => {
    host.stack.mockReturnValueOnce(throwError(() => new Error('network down')));

    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('The host did not answer: network down');
    expect(fixture.nativeElement.querySelectorAll('tbody tr').length).toBe(0);

    await vi.advanceTimersByTimeAsync(3000);
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('tbody tr')) as HTMLElement[];
    expect(rows.length).toBe(2);
    expect(fixture.nativeElement.textContent).not.toContain('The host did not answer');
  });

  it('shows the problem detail when the host refuses a wipe', async () => {
    host.backendDown.mockReturnValueOnce(
      throwError(() => ({
        error: { title: 'Confirmation required', detail: 'Wiping volumes destroys databases and broker state.' },
      })),
    );

    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    fixture.componentInstance.confirmText.set('down -v');
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('button.wipe') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Wiping volumes destroys databases and broker state.');
  });
});
