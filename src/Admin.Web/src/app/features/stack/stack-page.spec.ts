import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Subject, delay, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { JobEvent, SseClient } from '../../core/host/sse-client';
import { PageJobs } from '../../workspace/page-jobs';
import { StackPage } from './stack-page';
import { StackControlsWidget } from './widgets/stack-controls-widget';

const stack = {
  backend: {
    reachable: true,
    error: null,
    services: [
      { service: 'gateway', state: 'running', health: 'healthy', exitCode: 0, publishedPorts: [5000] },
      { service: 'catalog-migrator', state: 'exited', health: null, exitCode: 0, publishedPorts: [] },
    ],
  },
  frontend: { job: null, installed: true, deviceJob: null },
  reachability: [
    { name: 'gateway', url: 'http://localhost:5000/health/ready', up: true, status: 200 },
    { name: 'client', url: 'http://localhost:5173/', up: false, status: null },
  ],
  lease: null,
  loadRun: null,
};

const lease = {
  id: 'abc',
  changes: ['short-waits', 'stop-inventory'],
  title: "The saga's waits shortened + Inventory stopped",
  services: ['ordering-api', 'inventory-api'],
  label: 'Stock timeout',
  since: '2026-10-16T08:00:00Z',
  expiresAt: '2026-10-16T08:05:00Z',
  applyJob: 'job-9',
  restoreError: null,
};

const config = {
  backendDir: '', frontendDir: '/work/blueprint-frontend', composeFile: '', fakePlatform: true,
  urls: { gateway: 'http://localhost:5000', catalog: '', ordering: '', bff: '', inventory: '', payments: '', paymentSimulator: '', carrierSimulator: '', keycloak: 'http://localhost:8080', grafana: 'http://localhost:3000', client: 'http://localhost:5173' },
};

const health = {
  reachable: true,
  error: null,
  services: [
    {
      service: 'Catalog.Api',
      requestRate: 1.2,
      errorRatio: 0,
      latencyP99Seconds: 0.048,
      domainRefusalRate: 0,
      unauthorisedRate: 0.2,
      commandP95Seconds: 0.012,
      queryP95Seconds: null,
    },
    {
      service: 'Gateway.Api',
      requestRate: 2.4,
      errorRatio: null,
      latencyP99Seconds: 0.09,
      domainRefusalRate: 0.75,
      unauthorisedRate: 0,
      commandP95Seconds: null,
      queryP95Seconds: null,
    },
  ],
};

const alerts = {
  reachable: true,
  error: null,
  loaded: [
    { name: 'ErrorQueueDepth', group: 'platform-infrastructure', state: 'firing', health: 'ok', severity: 'page', summary: 'Messages in error queue', alerts: 1 },
    { name: 'Latency', group: 'platform-golden-signals', state: 'pending', health: 'ok', severity: 'page', summary: null, alerts: 1 },
    { name: 'OutboxGrowth', group: 'platform-messaging', state: 'inactive', health: 'err', severity: null, summary: null, alerts: 0 },
  ],
  waiting: [
    { name: 'StuckSaga', instruments: ['ordering_saga_oldest_unfinalised_age_seconds'] },
    { name: 'CacheHitRatioCollapse', instruments: ['hybrid_cache_hits_total', 'hybrid_cache_misses_total'] },
  ],
};

const doctor = {
  checks: [
    { name: 'Docker', state: 'Ok', detail: 'Docker 29.7.2 is answering.' },
    { name: 'Ports', state: 'Problem', detail: 'Held by another program, so Up cannot publish them: 5672, 15672 by erl (pid 7376).' },
    { name: 'Node', state: 'Unknown', detail: 'Node 22.23.2; the frontend clone\'s .nvmrc could not be read: not found' },
  ],
};

const runningJob = { id: 'fe-1', commandLine: 'npm start', state: 'Running', exitCode: null, startedAt: '' };
const deviceJob = { id: 'dev-1', commandLine: 'npm run emulator:android', state: 'Running', exitCode: null, startedAt: '' };
const loadJob = { id: 'load-1', commandLine: 'load-run k6 prometheus slo-run token product restock run', state: 'Running', exitCode: null, startedAt: '' };

const deviceReads = {
  checks: [
    { name: 'Emulator attached', state: 'Problem', detail: 'No emulator is attached. Start the AVD from Android Studio\'s Device Manager; the console does not.' },
    { name: 'Gateway admits the app\'s origin', state: 'Ok', detail: 'The gateway admits https://localhost.' },
    { name: 'Realm redirect for mobile-app', state: 'Unknown', detail: 'Keycloak\'s admin API did not answer: refused' },
  ],
};

function text(el: Element | null): string {
  return el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

/** The job the page shows: the screen renders outside the grid, so its widgets share the page with no id. */
function shownJob(): string | null {
  return TestBed.inject(PageJobs).of('')();
}

function controls(fixture: ComponentFixture<StackPage>): StackControlsWidget {
  return fixture.debugElement.query(By.directive(StackControlsWidget)).componentInstance as StackControlsWidget;
}

describe('StackPage', () => {
  let host: {
    stack: ReturnType<typeof vi.fn>;
    config: ReturnType<typeof vi.fn>;
    backendUp: ReturnType<typeof vi.fn>;
    backendDown: ReturnType<typeof vi.fn>;
    backendReset: ReturnType<typeof vi.fn>;
    frontendStart: ReturnType<typeof vi.fn>;
    frontendStop: ReturnType<typeof vi.fn>;
    frontendDeviceBuild: ReturnType<typeof vi.fn>;
    frontendDeviceReads: ReturnType<typeof vi.fn>;
    leaseRestore: ReturnType<typeof vi.fn>;
    loadRunStart: ReturnType<typeof vi.fn>;
    loadRunStop: ReturnType<typeof vi.fn>;
    telemetryHealth: ReturnType<typeof vi.fn>;
    telemetryAlerts: ReturnType<typeof vi.fn>;
    doctor: ReturnType<typeof vi.fn>;
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
      backendReset: vi.fn(() => of({ id: 'reset-1', commandLine: 'reset down -v up -d --wait readiness', state: 'Running', exitCode: null, startedAt: '' })),
      frontendStart: vi.fn(() => of({ id: 'fe-1', commandLine: 'npm start', state: 'Running', exitCode: null, startedAt: '' })),
      frontendStop: vi.fn(() => of({ id: 'fe-1', commandLine: 'npm start', state: 'Exited', exitCode: -1, startedAt: '' })),
      frontendDeviceBuild: vi.fn(() => of(deviceJob)),
      frontendDeviceReads: vi.fn(() => of(deviceReads)),
      leaseRestore: vi.fn(() => of({ lease: null, changes: [] })),
      loadRunStart: vi.fn(() => of(loadJob)),
      loadRunStop: vi.fn(() => of({ ...loadJob, state: 'Exited', exitCode: -1 })),
      telemetryHealth: vi.fn(() => of(health)),
      telemetryAlerts: vi.fn(() => of(alerts)),
      doctor: vi.fn(() => of(doctor)),
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

    const rows = Array.from(fixture.nativeElement.querySelectorAll('table.services tbody tr')) as HTMLElement[];
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
    expect(shownJob()).toBe('up-1');
  });

  describe('a Compose job started from the actions', () => {
    let events: Subject<JobEvent>;

    const line = (sequence: number, stream: 'Stdout' | 'Stderr', t: string): JobEvent => ({ kind: 'line', line: { sequence, at: '', stream, text: t } });

    async function openAndPressUp() {
      events = new Subject<JobEvent>();
      TestBed.overrideProvider(SseClient, { useValue: { follow: () => events.asObservable() } });
      const fixture = TestBed.createComponent(StackPage);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      (fixture.nativeElement.querySelector('button.up') as HTMLButtonElement).click();
      fixture.detectChanges();
      return fixture;
    }

    it('puts the output directly under the actions, before the reachability strip', async () => {
      const fixture = await openAndPressUp();

      const actions = fixture.nativeElement.querySelector('section.actions') as HTMLElement;
      const pane = fixture.nativeElement.querySelector('app-output-pane') as HTMLElement;
      const strip = fixture.nativeElement.querySelector('.reachability') as HTMLElement;
      expect(actions.compareDocumentPosition(pane) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(pane.compareDocumentPosition(strip) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    });

    it('counts the job up on its own button, disables the four Compose buttons, and reports the outcome when it exits', async () => {
      const fixture = await openAndPressUp();
      const button = (cls: string) => fixture.nativeElement.querySelector(`button.${cls}`) as HTMLButtonElement;

      expect(text(button('up'))).toBe('Up… 0:00');
      expect(text(button('down'))).toBe('Down');
      expect([button('up'), button('down'), button('wipe'), button('reset')].every((b) => b.disabled)).toBe(true);

      await vi.advanceTimersByTimeAsync(65_000);
      fixture.detectChanges();
      expect(text(button('up'))).toBe('Up… 1:05');

      events.next({ kind: 'exited', exitCode: 0 });
      fixture.detectChanges();
      expect(text(button('up'))).toBe('Up');
      expect(button('up').disabled).toBe(false);
      expect(text(fixture.nativeElement.querySelector('.job-outcome'))).toBe('Up finished: exit 0 in 1:05');
    });

    it('says a job failed, and keeps a failure apart from success', async () => {
      const fixture = await openAndPressUp();

      events.next({ kind: 'exited', exitCode: 1 });
      fixture.detectChanges();

      const outcome = fixture.nativeElement.querySelector('.job-outcome') as HTMLElement;
      expect(text(outcome)).toBe('Up failed: exit 1 in 0:00');
      expect(outcome.classList.contains('failed')).toBe(true);
    });

    it('marks only error-bearing Compose stderr lines, and lists each container at its last verb', async () => {
      const fixture = await openAndPressUp();

      events.next(line(0, 'Stderr', ' Container commerce-sql-1  Creating'));
      events.next(line(1, 'Stderr', ' Container commerce-sql-1  Healthy'));
      events.next(line(2, 'Stderr', ' Container commerce-gateway-1  Waiting'));
      events.next(line(3, 'Stderr', ' Container commerce-gateway-1 is unhealthy'));
      fixture.detectChanges();

      const marked = Array.from(fixture.nativeElement.querySelectorAll('pre .stderr')) as HTMLElement[];
      expect(marked.map((m) => text(m))).toEqual(['[stderr] Container commerce-gateway-1 is unhealthy']);
      expect(text(fixture.nativeElement.querySelector('.compose-count'))).toBe('1 of 2 services settled');
      expect(Array.from(fixture.nativeElement.querySelectorAll('.compose-progress li')).map((li) => text(li as Element))).toEqual([
        'commerce-sql-1 Healthy',
        'commerce-gateway-1 Waiting',
      ]);
    });

    it('holds the buttons while the page shows another job, since the Compose job is followed on its own stream', async () => {
      const fixture = await openAndPressUp();

      TestBed.inject(PageJobs).show('', 'other');
      fixture.detectChanges();
      const up = fixture.nativeElement.querySelector('button.up') as HTMLButtonElement;
      expect(up.disabled).toBe(true);

      events.next({ kind: 'exited', exitCode: 0 });
      fixture.detectChanges();
      expect(up.disabled).toBe(false);
      expect(text(fixture.nativeElement.querySelector('.job-outcome'))).toBe('Up finished: exit 0 in 0:00');
    });

    it('frees the buttons when the job stream fails, since no exit will come', async () => {
      const fixture = await openAndPressUp();

      events.error(new Error('The host closed the job stream.'));
      fixture.detectChanges();

      expect((fixture.nativeElement.querySelector('button.up') as HTMLButtonElement).disabled).toBe(false);
    });

    it('keeps a job started after a failed stream running', async () => {
      const fixture = await openAndPressUp();
      events.error(new Error('The host closed the job stream.'));
      fixture.detectChanges();

      events = new Subject<JobEvent>();
      const up = fixture.nativeElement.querySelector('button.up') as HTMLButtonElement;
      up.click();
      fixture.detectChanges();

      expect(up.disabled).toBe(true);
      expect(text(up)).toMatch(/^Up… /);
    });
  });

  it('keeps the wipe button disabled until the confirmation is typed', async () => {
    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    const wipe = fixture.nativeElement.querySelector('button.wipe') as HTMLButtonElement;
    expect(wipe.disabled).toBe(true);

    controls(fixture).confirmText.set('down -v');
    fixture.detectChanges();
    expect(wipe.disabled).toBe(false);

    wipe.click();
    expect(host.backendDown).toHaveBeenCalledWith(true, 'down -v');
  });

  it('keeps Reset behind the same typed confirmation and hands its job to the output pane', async () => {
    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    const reset = fixture.nativeElement.querySelector('button.reset') as HTMLButtonElement;
    expect(reset.disabled).toBe(true);

    controls(fixture).confirmText.set('down -v');
    fixture.detectChanges();
    expect(reset.disabled).toBe(false);

    reset.click();
    fixture.detectChanges();
    expect(host.backendReset).toHaveBeenCalledWith('down -v');
    expect(shownJob()).toBe('reset-1');
    expect(controls(fixture).confirmText()).toBe('');
  });

  it('gives the wipe confirmation input an accessible name', async () => {
    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    const confirm = fixture.nativeElement.querySelector('input[placeholder="type: down -v"]') as HTMLInputElement;
    expect(confirm.getAttribute('aria-label')).toBe('Type down -v to confirm wiping volumes');
  });

  describe('workstation doctor', () => {
    async function render() {
      const fixture = TestBed.createComponent(StackPage);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
      return fixture;
    }

    it('reads the workstation once on opening and prints each check with its verdict in words', async () => {
      const fixture = await render();

      const rows = Array.from(fixture.nativeElement.querySelectorAll('table.doctor tbody tr')) as HTMLElement[];
      expect(rows.map((r) => text(r))).toEqual([
        'Docker ok Docker 29.7.2 is answering.',
        'Ports problem Held by another program, so Up cannot publish them: 5672, 15672 by erl (pid 7376).',
        "Node could not tell Node 22.23.2; the frontend clone's .nvmrc could not be read: not found",
      ]);
      expect(rows.map((r) => r.className)).toEqual(['state-Ok', 'state-Problem', 'state-Unknown']);
      expect(host.doctor).toHaveBeenCalledTimes(1);
    });

    it('is not polled: it reads again only when asked', async () => {
      const fixture = await render();

      await vi.advanceTimersByTimeAsync(60000);
      expect(host.doctor).toHaveBeenCalledTimes(1);

      (fixture.nativeElement.querySelector('button.check') as HTMLButtonElement).click();
      await vi.advanceTimersByTimeAsync(0);
      expect(host.doctor).toHaveBeenCalledTimes(2);
    });

    it('shows the host error and keeps the last checks when a read fails', async () => {
      const fixture = await render();
      host.doctor.mockReturnValueOnce(throwError(() => new Error('network down')));

      (fixture.nativeElement.querySelector('button.check') as HTMLButtonElement).click();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();

      expect(text(fixture.nativeElement.querySelector('section.doctor'))).toContain('The host did not answer for the workstation: network down');
      expect(fixture.nativeElement.querySelectorAll('table.doctor tbody tr').length).toBe(3);
    });
  });

  describe('golden signals', () => {
    async function render() {
      const fixture = TestBed.createComponent(StackPage);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
      return fixture;
    }

    it('prints every dashboard panel per service under a header naming each, with a dash where the value is unknown', async () => {
      const fixture = await render();

      const headers = Array.from(fixture.nativeElement.querySelectorAll('table.signals th')) as HTMLElement[];
      expect(headers.map((h) => text(h))).toEqual([
        'Service',
        'Requests (req/s)',
        '5xx share',
        'Latency p99',
        '422 (req/s)',
        '401 (req/s)',
        'Command p95',
        'Query p95',
      ]);
      const rows = Array.from(fixture.nativeElement.querySelectorAll('table.signals tbody tr')) as HTMLElement[];
      expect(rows.map((r) => text(r))).toEqual([
        'Catalog.Api 1.2 0.0 % 48 ms 0 0.2 12 ms —',
        'Gateway.Api 2.4 — 90 ms 0.75 0 — —',
      ]);
    });

    it('says the signals are unavailable, with the reason, when the host reports them unreachable', async () => {
      host.telemetryHealth.mockReturnValue(of({ reachable: false, error: 'Prometheus answered 400: parse error', services: [] }));
      const fixture = await render();

      expect(text(fixture.nativeElement.querySelector('.signals'))).toBe('Golden signals unavailable: Prometheus answered 400: parse error');
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
      expect(fixture.nativeElement.querySelectorAll('table.signals tbody tr').length).toBe(2);
      expect(text(fixture.nativeElement.querySelector('.signals'))).not.toContain('network down');
    });
  });

  describe('alert strip', () => {
    async function render() {
      const fixture = TestBed.createComponent(StackPage);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
      return fixture;
    }

    it('lists each loaded rule with its state, severity and instances, and the waiting rules apart under what they wait on', async () => {
      const fixture = await render();

      const loaded = Array.from(fixture.nativeElement.querySelectorAll('table.alerts tbody tr')) as HTMLElement[];
      expect(loaded.map((r) => text(r))).toEqual([
        'ErrorQueueDepth firing page 1',
        'Latency pending page 1',
        'OutboxGrowth inactive (evaluation failing) — 0',
      ]);
      expect(loaded.map((r) => r.className)).toEqual(['state-firing', 'state-pending', 'state-inactive']);

      const waiting = Array.from(fixture.nativeElement.querySelectorAll('table.waiting tbody tr')) as HTMLElement[];
      expect(waiting.map((r) => text(r))).toEqual([
        'StuckSaga ordering_saga_oldest_unfinalised_age_seconds',
        'CacheHitRatioCollapse hybrid_cache_hits_total, hybrid_cache_misses_total',
      ]);
      expect(text(fixture.nativeElement.querySelector('.alerts-waiting'))).toContain('Waiting, not quiet');
      expect(text(fixture.nativeElement.querySelector('table.waiting'))).not.toMatch(/inactive|quiet|ok/i);
    });

    it('says an empty list is not a quiet platform, and still lists the waiting rules', async () => {
      host.telemetryAlerts.mockReturnValue(of({ ...alerts, loaded: [] }));
      const fixture = await render();

      expect(text(fixture.nativeElement.querySelector('.alerts-empty'))).toContain('not a quiet platform');
      expect(fixture.nativeElement.querySelectorAll('table.waiting tbody tr').length).toBe(2);
    });

    it('says the rules are unavailable, with the reason, and still lists the waiting rules', async () => {
      host.telemetryAlerts.mockReturnValue(of({ ...alerts, reachable: false, error: 'Prometheus answered 502: bad gateway', loaded: [] }));
      const fixture = await render();

      expect(text(fixture.nativeElement.querySelector('.alerts .error'))).toBe('Alert rules unavailable: Prometheus answered 502: bad gateway');
      expect(fixture.nativeElement.querySelector('table.alerts')).toBeNull();
      expect(fixture.nativeElement.querySelectorAll('table.waiting tbody tr').length).toBe(2);
    });

    it('polls on the slower timer and recovers after a failed read', async () => {
      host.telemetryAlerts.mockReturnValueOnce(throwError(() => new Error('network down')));
      const fixture = await render();

      expect(text(fixture.nativeElement.querySelector('.alerts'))).toContain('network down');

      await vi.advanceTimersByTimeAsync(15000);
      fixture.detectChanges();

      expect(host.telemetryAlerts).toHaveBeenCalledTimes(2);
      expect(text(fixture.nativeElement.querySelector('.alerts'))).not.toContain('network down');
      expect(fixture.nativeElement.querySelectorAll('table.alerts tbody tr').length).toBe(3);
    });
  });

  it('keeps polling after a failed request', async () => {
    host.stack.mockReturnValueOnce(throwError(() => new Error('network down')));

    const fixture = TestBed.createComponent(StackPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('The host did not answer: network down');
    expect(fixture.nativeElement.querySelectorAll('table.services tbody tr').length).toBe(0);

    await vi.advanceTimersByTimeAsync(3000);
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('table.services tbody tr')) as HTMLElement[];
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
    expect(fixture.nativeElement.querySelectorAll('table.services tbody tr').length).toBe(0);

    await vi.advanceTimersByTimeAsync(2000);
    fixture.detectChanges();

    expect(host.stack).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelectorAll('table.services tbody tr').length).toBe(2);
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

    controls(fixture).confirmText.set('down -v');
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('button.wipe') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Wiping volumes destroys databases and broker state.');
  });

  describe('load run', () => {
    async function render() {
      const fixture = TestBed.createComponent(StackPage);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
      const q = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLButtonElement | null;
      return { fixture, q };
    }

    it('says a local run proves nothing about the SLO, and offers Run with Stop disabled', async () => {
      const { q } = await render();

      expect(text(q('.load-run-status'))).toBe('k6 run slo.js not run');
      expect(text(q('.load-run-note'))).toContain('A local run is not a staging baseline and proves nothing about the SLO (§13.7).');
      expect(q('button.load-run-start')!.disabled).toBe(false);
      expect(q('button.load-run-stop')!.disabled).toBe(true);
      expect(q('button.load-run-output')).toBeNull();
    });

    it('starts a run and hands its job to the output pane', async () => {
      const { fixture, q } = await render();

      q('button.load-run-start')!.click();
      fixture.detectChanges();

      expect(host.loadRunStart).toHaveBeenCalled();
      expect(shownJob()).toBe('load-1');
    });

    it('disables Run and enables Stop while a run goes, and Stop calls the host', async () => {
      host.stack.mockReturnValue(of({ ...stack, loadRun: loadJob }));
      const { fixture, q } = await render();

      expect(text(q('.load-run-status'))).toBe('k6 run slo.js running');
      expect(q('button.load-run-start')!.disabled).toBe(true);
      expect(q('button.load-run-stop')!.disabled).toBe(false);

      q('button.load-run-stop')!.click();
      fixture.detectChanges();

      expect(host.loadRunStop).toHaveBeenCalled();
    });

    it('shows the exit code of a run that ended and opens its output', async () => {
      host.stack.mockReturnValue(of({ ...stack, loadRun: { ...loadJob, state: 'Exited', exitCode: 99 } }));
      const { q } = await render();

      expect(text(q('.load-run-status'))).toBe('k6 run slo.js exited 99');
      expect(q('button.load-run-start')!.disabled).toBe(false);

      q('button.load-run-output')!.click();

      expect(shownJob()).toBe('load-1');
    });

    it('shows why the host refused a Stop', async () => {
      host.stack.mockReturnValue(of({ ...stack, loadRun: loadJob }));
      host.loadRunStop.mockReturnValue(throwError(() => ({ error: { title: 'No load run going', detail: 'There is no load run to stop.' } })));
      const { fixture, q } = await render();

      q('button.load-run-stop')!.click();
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).toContain('There is no load run to stop.');
    });
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

    it('says why Start is off when node_modules differs from the lock, in the host\'s words (#369)', async () => {
      const problem =
        'node_modules does not match package-lock.json (installed 2026-09-12, lock changed 2026-10-20): ' +
        '@angular/core 22.1.6 is installed (the lock has 22.2.3). Run npm ci in C:/dev/blueprint-frontend; the console does not.';
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: null, installed: false, deviceJob: null, installProblem: problem } }));
      const { fixture, q } = await render();

      expect(text(q('p.error'))).toBe(problem);
      expect(q('button.frontend-start')!.disabled).toBe(true);
      expect(q('button.device-build')!.disabled).toBe(true);
      expect(fixture.nativeElement.textContent).not.toContain('has no node_modules');
    });

    it('falls back to the missing-node_modules sentence when the host names no reason', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: null, installed: false, deviceJob: null } }));
      const { q } = await render();

      expect(text(q('p.error'))).toContain('has no node_modules. Run npm ci there; the console does not.');
    });

    it('starts the frontend and hands its job to the output pane', async () => {
      const { fixture, q } = await render();

      q('button.frontend-start')!.click();
      fixture.detectChanges();

      expect(host.frontendStart).toHaveBeenCalled();
      expect(shownJob()).toBe('fe-1');
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
      expect(text(q('.frontend-stop-note'))).toContain('Playwright run reuses');

      q('button.frontend-stop')!.click();
      fixture.detectChanges();

      expect(host.frontendStop).toHaveBeenCalled();
      expect(shownJob()).toBe('fe-1');
    });

    it('shows the exit code of a job that ended', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: { ...runningJob, state: 'Exited', exitCode: 1 }, installed: true } }));
      const { q } = await render();

      expect(text(q('.frontend-status'))).toContain('npm start exited 1');
      expect(q('button.frontend-start')!.disabled).toBe(false);
    });

    it('opens the output of the last job on Show output', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: runningJob, installed: true } }));
      const { q } = await render();

      q('button.frontend-output')!.click();

      expect(shownJob()).toBe('fe-1');
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

  describe('scenario lease', () => {
    async function render() {
      const fixture = TestBed.createComponent(StackPage);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
      const q = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLButtonElement | null;
      return { fixture, q };
    }

    it('shows nothing when no lease is held', async () => {
      const { q } = await render();

      expect(q('section.lease')).toBeNull();
    });

    it('says what a Scenario changed, when it undoes it by itself, and offers Restore', async () => {
      host.stack.mockReturnValue(of({ ...stack, lease }));
      const { q } = await render();

      expect(text(q('section.lease h3'))).toBe("The saga's waits shortened + Inventory stopped");
      expect(text(q('.lease-what'))).toBe(
        'A Scenario (Stock timeout) changed ordering-api, inventory-api at 2026-10-16T08:00:00Z. It undoes it by itself at 2026-10-16T08:05:00Z.',
      );
      expect(q('button.lease-restore')!.disabled).toBe(false);
    });

    it('restores on request and opens the change\'s output', async () => {
      host.stack.mockReturnValue(of({ ...stack, lease }));
      const { q } = await render();

      q('button.lease-restore')!.click();
      q('button.lease-output')!.click();

      expect(host.leaseRestore).toHaveBeenCalledTimes(1);
      expect(shownJob()).toBe('job-9');
    });

    it('shows why a restore failed, and keeps the button for another try', async () => {
      host.stack.mockReturnValue(of({ ...stack, lease: { ...lease, restoreError: 'inventory-api is not running after the restore.' } }));
      host.leaseRestore.mockReturnValueOnce(throwError(() => ({ error: { detail: 'docker did not answer' } })));
      const { fixture, q } = await render();

      expect(text(q('section.lease .error'))).toBe('The last restore failed: inventory-api is not running after the restore.');

      q('button.lease-restore')!.click();
      fixture.detectChanges();

      expect(text(fixture.nativeElement.querySelector('section.lease'))).toContain('docker did not answer');
      expect(q('button.lease-restore')!.disabled).toBe(false);
    });
  });

  describe('device lane', () => {
    async function render() {
      const fixture = TestBed.createComponent(StackPage);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
      const q = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLButtonElement | null;
      return { fixture, q };
    }

    it('reads the emulator, the gateway and the realm when the screen opens and shows each verdict', async () => {
      const { fixture } = await render();

      const rows = Array.from(fixture.nativeElement.querySelectorAll('table.device-reads tbody tr')) as HTMLElement[];
      expect(rows.map((r) => text(r))).toEqual([
        "Emulator attached problem No emulator is attached. Start the AVD from Android Studio's Device Manager; the console does not.",
        "Gateway admits the app's origin ok The gateway admits https://localhost.",
        "Realm redirect for mobile-app could not tell Keycloak's admin API did not answer: refused",
      ]);
      expect(host.frontendDeviceReads).toHaveBeenCalledTimes(1);
    });

    it('reads again on request, and not while a read is in flight', async () => {
      const { fixture, q } = await render();

      q('button.device-read')!.click();
      fixture.detectChanges();

      expect(host.frontendDeviceReads).toHaveBeenCalledTimes(2);
    });

    it('keeps the last reads and says so when the host does not answer', async () => {
      const { fixture, q } = await render();
      host.frontendDeviceReads.mockReturnValueOnce(throwError(() => new Error('network down')));

      q('button.device-read')!.click();
      fixture.detectChanges();

      expect(text(fixture.nativeElement.querySelector('.frontend'))).toContain('The host did not answer for the device reads: network down');
      expect(fixture.nativeElement.querySelectorAll('table.device-reads tbody tr').length).toBe(3);
    });

    it('builds for the emulator and hands the job to the output pane', async () => {
      const { fixture, q } = await render();

      expect(text(q('.device-status'))).toBe('npm run emulator:android not run');
      expect(q('button.device-output')).toBeNull();

      q('button.device-build')!.click();
      fixture.detectChanges();

      expect(host.frontendDeviceBuild).toHaveBeenCalled();
      expect(shownJob()).toBe('dev-1');
    });

    it('disables the build while it runs, and shows the exit code of one that ended', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: null, installed: true, deviceJob } }));
      const { fixture, q } = await render();

      expect(text(q('.device-status'))).toBe('npm run emulator:android running');
      expect(q('button.device-build')!.disabled).toBe(true);

      q('button.device-output')!.click();
      expect(shownJob()).toBe('dev-1');

      // The poll is the page's, not the widget's: only once nothing reads the stack does a new screen read it afresh.
      fixture.destroy();
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: null, installed: true, deviceJob: { ...deviceJob, state: 'Exited', exitCode: 1 } } }));
      const ended = await render();

      expect(text(ended.q('.device-status'))).toBe('npm run emulator:android exited 1');
      expect(ended.q('button.device-build')!.disabled).toBe(false);
    });

    it('disables the build when the clone has no node_modules', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: null, installed: false, deviceJob: null } }));
      const { q } = await render();

      expect(q('button.device-build')!.disabled).toBe(true);
    });
  });
});
