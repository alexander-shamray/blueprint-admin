import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { AlertsWidget } from './alerts-widget';
import { ComposeServicesWidget } from './compose-services-widget';
import { GoldenSignalsWidget } from './golden-signals-widget';
import { ReachabilityWidget } from './reachability-widget';

const stack = {
  backend: {
    reachable: true,
    error: null,
    services: [
      { service: 'gateway', state: 'running', health: 'healthy', exitCode: 0, publishedPorts: [5000] },
      { service: 'sql', state: 'running', health: 'healthy', exitCode: 0, publishedPorts: [1433] },
    ],
  },
  frontend: { job: null, installed: true, deviceJob: null },
  reachability: [
    { name: 'gateway', url: 'http://localhost:5000/health/ready', up: true, status: 200, published: true },
    { name: 'grafana', url: 'http://localhost:3000', up: false, status: null, published: true },
    { name: 'ordering', url: 'http://localhost:5101/health/ready', up: false, status: null, published: false },
  ],
  lease: null,
  loadRun: null,
  stressRun: null,
  stressOverride: [],
};

const signal = (service: string) => ({
  service,
  requestRate: 1,
  errorRatio: 0,
  latencyP99Seconds: 0.01,
  domainRefusalRate: 0,
  unauthorisedRate: 0,
  commandP95Seconds: null,
  queryP95Seconds: null,
});

const rule = (name: string, state: string) => ({
  name,
  group: 'g',
  state,
  health: 'ok',
  severity: 'page',
  summary: null,
  alerts: state === 'firing' ? 1 : 0,
});

function text(el: Element): string {
  return el.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

// Each widget against a mocked host, with the config its saved instance carries (widget-workspace spec §11).
describe('a Stack widget with a config', () => {
  let alerts: { reachable: boolean; error: null; loaded: ReturnType<typeof rule>[]; waiting: never[] };

  beforeEach(() => {
    vi.useFakeTimers();
    alerts = {
      reachable: true,
      error: null,
      loaded: [rule('ErrorQueueDepth', 'firing'), rule('Latency', 'pending')],
      waiting: [],
    };
    TestBed.configureTestingModule({
      providers: [
        {
          provide: HostClient,
          useValue: {
            stack: () => of(stack),
            telemetryHealth: () =>
              of({ reachable: true, error: null, services: [signal('Catalog.Api'), signal('Gateway.Api')] }),
            telemetryAlerts: () => of(alerts),
          },
        },
      ],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  async function render(component: Type<unknown>, config: Record<string, unknown>) {
    const fixture = TestBed.createComponent(component);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(1);
    fixture.detectChanges();
    return (selector: string) => [...(fixture.nativeElement as HTMLElement).querySelectorAll(selector)].map(text);
  }

  it('lists only the Compose services it names, and every one for none', async () => {
    const named = await render(ComposeServicesWidget, { services: ['sql'] });
    expect(named('table.services tbody tr')).toEqual(['sql running healthy 1433']);

    const every = await render(ComposeServicesWidget, { services: [] });
    expect(every('table.services tbody tr').map((r) => r.split(' ')[0])).toEqual(['gateway', 'sql']);
  });

  it('shows only the surfaces it names', async () => {
    const rows = await render(ReachabilityWidget, { surfaces: ['grafana'] });

    expect(rows('.reachability span')).toEqual(['grafana down']);
  });

  it('says a surface the stress override took the host port from has none, rather than that it is down', async () => {
    const rows = await render(ReachabilityWidget, { surfaces: ['ordering'] });

    expect(rows('.reachability span')).toEqual(['ordering no host port on the stress override']);
    expect(rows('.reachability span.down')).toEqual([]);
  });

  it('prints the golden signals of only the services it names', async () => {
    const rows = await render(GoldenSignalsWidget, { services: ['Gateway.Api'] });

    expect(rows('table.signals tbody tr').map((r) => r.split(' ')[0])).toEqual(['Gateway.Api']);
  });

  it('shows the firing rules only when asked, and says so when none is firing', async () => {
    const every = await render(AlertsWidget, { firingOnly: false });
    expect(every('table.alerts tbody tr').map((r) => r.split(' ')[0])).toEqual(['ErrorQueueDepth', 'Latency']);

    const firing = await render(AlertsWidget, { firingOnly: true });
    expect(firing('table.alerts tbody tr').map((r) => r.split(' ')[0])).toEqual(['ErrorQueueDepth']);

    alerts = { ...alerts, loaded: [rule('Latency', 'pending')] };
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: HostClient, useValue: { telemetryAlerts: () => of(alerts) } }],
    });
    const quiet = await render(AlertsWidget, { firingOnly: true });
    expect(quiet('.alerts-empty')).toEqual(['No rule is firing.']);
    expect(quiet('table.alerts')).toEqual([]);
  });
});
