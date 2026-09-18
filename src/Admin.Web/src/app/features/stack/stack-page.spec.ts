import { TestBed } from '@angular/core/testing';
import { delay, of, throwError } from 'rxjs';
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
  frontend: { job: null, installed: true },
  reachability: [
    { name: 'gateway', url: 'http://localhost:5000/health/ready', up: true, status: 200 },
    { name: 'client', url: 'http://localhost:5173/', up: false, status: null },
  ],
};

const config = {
  backendDir: '', frontendDir: '/work/blueprint-frontend', composeFile: '', fakePlatform: true,
  urls: { gateway: 'http://localhost:5000', catalog: '', ordering: '', bff: '', keycloak: 'http://localhost:8080', grafana: 'http://localhost:3000', client: 'http://localhost:5173' },
};

const health = {
  reachable: true,
  error: null,
  services: [
    { service: 'Catalog.Api', requestRate: 1.2, errorRatio: null, latencyP99Seconds: 0.048 },
    { service: 'Gateway.Api', requestRate: 2.4, errorRatio: 0.05, latencyP99Seconds: 0.09 },
  ],
};

const runningJob ={ id: 'fe-1', commandLine: 'npm start', state: 'Running', exitCode: null, startedAt: '' };

function text(el: Element | null): string {
  return el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('StackPage', () => {
  let host: {
    stack: ReturnType<typeof vi.fn>;
    config: ReturnType<typeof vi.fn>;
    backendUp: ReturnType<typeof vi.fn>;
    backendDown: ReturnType<typeof vi.fn>;
    frontendStart: ReturnType<typeof vi.fn>;
    frontendStop: ReturnType<typeof vi.fn>;
    telemetryHealth: ReturnType<typeof vi.fn>;
  };

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
      frontendStart: vi.fn(() => of({ id: 'fe-1', commandLine: 'npm start', state: 'Running', exitCode: null, startedAt: '' })),
      frontendStop: vi.fn(() => of({ id: 'fe-1', commandLine: 'npm start', state: 'Exited', exitCode: -1, startedAt: '' })),
      telemetryHealth: vi.fn(() => of(health)),
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

  it('renders each reachability chip as name, up/down text and status, omitting status when unknown', async () => {
    host.stack.mockReturnValueOnce(
      of({
        ...stack,
        reachability: [
          { name: 'gateway', url: 'http://localhost:5000/health/ready', up: true, status: 200 },
          { name: 'grafana', url: 'http://localhost:3000', up: false, status: null },
        ],
      }),
    );

    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    const chips = Array.from(fixture.nativeElement.querySelectorAll('.reachability span')) as HTMLElement[];
    expect(chips.map((c) => c.textContent?.replace(/\s+/g, ' ').trim())).toEqual(['gateway up 200', 'grafana down']);
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

  it('gives the wipe confirmation input an accessible name', async () => {
    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    const confirm = fixture.nativeElement.querySelector('input[placeholder="type: down -v"]') as HTMLInputElement;
    expect(confirm.getAttribute('aria-label')).toBe('Type down -v to confirm wiping volumes');
  });

  describe('golden signals', () => {
    async function render() {
      const fixture = TestBed.createComponent(StackPage);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
      return fixture;
    }

    it('prints rate, 5xx share and p99 per service, with a dash where Prometheus had no value', async () => {
      const fixture = await render();

      const items = Array.from(fixture.nativeElement.querySelectorAll('.signals li')) as HTMLElement[];
      expect(items.map((i) => text(i))).toEqual([
        'Catalog.Api 1.20 req/s — p99 48 ms',
        'Gateway.Api 2.40 req/s 5.0 % 5xx p99 90 ms',
      ]);
    });

    it('says Prometheus did not answer when the host reports it unreachable', async () => {
      host.telemetryHealth.mockReturnValue(of({ reachable: false, error: 'Grafana has no Prometheus datasource.', services: [] }));
      const fixture = await render();

      expect(text(fixture.nativeElement.querySelector('.signals'))).toBe('Prometheus did not answer: Grafana has no Prometheus datasource.');
    });

    it('says no requests are recorded rather than showing an empty strip', async () => {
      host.telemetryHealth.mockReturnValue(of({ reachable: true, error: null, services: [] }));
      const fixture = await render();

      expect(text(fixture.nativeElement.querySelector('.signals'))).toBe('No requests recorded yet.');
    });

    it('polls on its own slower timer and recovers after a failed read', async () => {
      host.telemetryHealth.mockReturnValueOnce(throwError(() => new Error('network down')));
      const fixture = await render();

      expect(text(fixture.nativeElement.querySelector('.signals'))).toContain('network down');

      await vi.advanceTimersByTimeAsync(3000);
      expect(host.telemetryHealth).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(12000);
      fixture.detectChanges();

      expect(host.telemetryHealth).toHaveBeenCalledTimes(2);
      expect(fixture.nativeElement.querySelectorAll('.signals li').length).toBe(2);
      expect(text(fixture.nativeElement.querySelector('.signals'))).not.toContain('network down');
    });
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

  it('lets a slow poll finish instead of cancelling it on the next tick', async () => {
    // docker compose ps can take longer than the 3 s tick; a cancelled request kills the ps on
    // the host, so if each tick replaced the last one no answer would ever arrive.
    host.stack.mockImplementation(() => of(stack).pipe(delay(5000)));

    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(3000);
    fixture.detectChanges();

    expect(host.stack).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelectorAll('tbody tr').length).toBe(0);

    await vi.advanceTimersByTimeAsync(2000);
    fixture.detectChanges();

    expect(host.stack).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelectorAll('tbody tr').length).toBe(2);
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

  describe('reference client', () => {
    async function render() {
      const fixture = TestBed.createComponent(StackPage);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
      const q = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLButtonElement | null;
      return { fixture, q };
    }

    it('shows a frontend that was never started, with Start enabled and Stop disabled', async () => {
      const { fixture, q } = await render();

      expect(text(q('.frontend-status'))).toBe('npm start not started client does not answer');
      expect(q('button.frontend-start')!.disabled).toBe(false);
      expect(q('button.frontend-stop')!.disabled).toBe(true);
      expect(q('button.frontend-output')).toBeNull();
      expect(fixture.nativeElement.textContent).not.toContain('npm ci');
    });

    it('starts the frontend and hands its job to the output pane', async () => {
      const { fixture, q } = await render();

      q('button.frontend-start')!.click();
      fixture.detectChanges();

      expect(host.frontendStart).toHaveBeenCalled();
      expect(fixture.componentInstance.jobId()).toBe('fe-1');
    });

    it('disables Start and enables Stop while npm start runs, and Stop calls the host', async () => {
      host.stack.mockReturnValue(
        of({
          ...stack,
          frontend: { job: runningJob, installed: true },
          reachability: [{ name: 'client', url: 'http://localhost:5173/', up: true, status: 200 }],
        }),
      );
      const { fixture, q } = await render();

      expect(text(q('.frontend-status'))).toBe('npm start running client answers');
      expect(q('button.frontend-start')!.disabled).toBe(true);
      expect(q('button.frontend-stop')!.disabled).toBe(false);

      q('button.frontend-stop')!.click();
      fixture.detectChanges();

      expect(host.frontendStop).toHaveBeenCalled();
      expect(fixture.componentInstance.jobId()).toBe('fe-1');
    });

    it('shows the exit code of a job that ended', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: { ...runningJob, state: 'Exited', exitCode: 1 }, installed: true } }));
      const { q } = await render();

      expect(text(q('.frontend-status'))).toContain('npm start exited 1');
      expect(q('button.frontend-start')!.disabled).toBe(false);
    });

    it('opens the output of the last job on Show output', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: runningJob, installed: true } }));
      const { fixture, q } = await render();

      q('button.frontend-output')!.click();

      expect(fixture.componentInstance.jobId()).toBe('fe-1');
    });

    it('says npm ci is needed and disables Start when node_modules is absent', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: null, installed: false } }));
      const { fixture, q } = await render();

      expect(fixture.nativeElement.textContent).toContain('/work/blueprint-frontend has no node_modules. Run npm ci there; the console does not.');
      expect(q('button.frontend-start')!.disabled).toBe(true);
    });

    it("shows the host's refusal when a start conflicts", async () => {
      host.frontendStart.mockReturnValueOnce(
        throwError(() => ({ error: { title: 'Frontend already running', detail: 'npm start is job fe-0. Stop it first.' } })),
      );
      const { fixture, q } = await render();

      q('button.frontend-start')!.click();
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).toContain('npm start is job fe-0. Stop it first.');
    });
  });
});
