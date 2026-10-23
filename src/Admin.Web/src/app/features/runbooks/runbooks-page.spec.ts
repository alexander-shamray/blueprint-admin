import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { AlertStripView, JobSummary, RunbookCatalogue } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';
import { By } from '@angular/platform-browser';
import { RunbooksPage } from './runbooks-page';
import { ALERT_POLL_MS, RunbooksWidget } from './widgets/runbooks-widget';

const catalogue: RunbookCatalogue = {
  error: null,
  runbooks: [
    {
      runbook: 'error-queue',
      alerts: ['ErrorQueueDepth'],
      script: true,
      reason: null,
      phases: [{ alert: 'ErrorQueueDepth', impact: 'none', confirmation: 'none', confirmWith: null, sustained: false, firesWithinSeconds: 240, clearsWithinSeconds: 240 }],
    },
    {
      runbook: 'outbox-growth',
      alerts: ['OutboxGrowth'],
      script: true,
      reason: null,
      phases: [{ alert: 'OutboxGrowth', impact: 'outage', confirmation: 'confirm', confirmWith: 'cause', sustained: true, firesWithinSeconds: 780, clearsWithinSeconds: 780 }],
    },
    {
      runbook: 'contact-refused',
      alerts: ['ContactReadRefused'],
      script: true,
      reason: null,
      phases: [{ alert: 'ContactReadRefused', impact: 'data', confirmation: 'typed', confirmWith: 'contact-refused', sustained: false, firesWithinSeconds: 300, clearsWithinSeconds: 1980 }],
    },
    {
      runbook: 'error-rate',
      alerts: ['ErrorRateGateway', 'ErrorRateService'],
      script: true,
      reason: null,
      phases: [
        { alert: 'ErrorRateGateway', impact: 'outage', confirmation: 'confirm', confirmWith: 'cause', sustained: true, firesWithinSeconds: 480, clearsWithinSeconds: 480 },
        { alert: 'ErrorRateService', impact: 'outage', confirmation: 'confirm', confirmWith: 'cause', sustained: true, firesWithinSeconds: 480, clearsWithinSeconds: 480 },
      ],
    },
    { runbook: 'stuck-saga', alerts: ['StuckSaga'], script: false, reason: 'its rule is not loaded', phases: [] },
  ],
};

function strip(state: string): AlertStripView {
  return {
    reachable: true,
    error: null,
    waiting: [],
    loaded: [
      { name: 'ErrorQueueDepth', group: 'g', state: 'inactive', health: 'ok', severity: null, summary: null, alerts: 0 },
      { name: 'OutboxGrowth', group: 'g', state, health: 'ok', severity: null, summary: null, alerts: state === 'firing' ? 1 : 0 },
    ],
  };
}

const job: JobSummary = { id: 'j-1', command: 'py', state: 'Running', exitCode: null } as unknown as JobSummary;

describe('RunbooksPage', () => {
  let host: {
    runbooks: ReturnType<typeof vi.fn>;
    runbookText: ReturnType<typeof vi.fn>;
    runbookCause: ReturnType<typeof vi.fn>;
    runbookRestore: ReturnType<typeof vi.fn>;
    telemetryAlerts: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    vi.useFakeTimers();
    host = {
      runbooks: vi.fn(() => of(catalogue)),
      runbookText: vi.fn(() => of({ runbook: 'x', markdown: '# Runbook — x\n\nFollow `step one`.' })),
      runbookCause: vi.fn(() => of(job)),
      runbookRestore: vi.fn(() => of(job)),
      telemetryAlerts: vi.fn(() => of(strip('inactive'))),
    };
    TestBed.configureTestingModule({
      imports: [RunbooksPage],
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => of() } },
      ],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  /**
   * The Runbooks screen as the legacy screen widget renders it: the Runbooks widget. The tests below are the screen's,
   * from before it was a widget, so `componentInstance` is the widget they drive.
   */
  function render() {
    const fixture = TestBed.createComponent(RunbooksPage);
    fixture.detectChanges();
    vi.advanceTimersByTime(0);
    fixture.detectChanges();
    const widget = fixture.debugElement.query(By.directive(RunbooksWidget)).componentInstance as RunbooksWidget;
    return Object.defineProperty(fixture, 'componentInstance', { value: widget }) as unknown as ReturnType<
      typeof TestBed.createComponent<RunbooksWidget>
    >;
  }

  function el(fixture: ReturnType<typeof render>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function open(fixture: ReturnType<typeof render>, name: string): void {
    const button = Array.from(el(fixture).querySelectorAll('button.runbook')).find((b) => b.textContent?.trim().startsWith(name)) as HTMLButtonElement;
    button.click();
    fixture.detectChanges();
  }

  function button(fixture: ReturnType<typeof render>, selector: string): HTMLButtonElement {
    return el(fixture).querySelector(selector) as HTMLButtonElement;
  }

  it('lists every runbook the tool lists and marks one with no script in text', () => {
    const fixture = render();

    expect(Array.from(el(fixture).querySelectorAll('button.runbook')).map((b) => b.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'error-queue',
      'outbox-growth',
      'contact-refused',
      'error-rate',
      'stuck-saga [no script]',
    ]);
  });

  it('says why the listing failed instead of an empty list', () => {
    host.runbooks.mockReturnValue(of({ runbooks: [], error: 'py: not found' }));
    const fixture = render();

    expect(el(fixture).querySelector('[role="alert"]')?.textContent).toContain('py: not found');
  });

  it('renders a nested list item as a list inside the item it belongs to', () => {
    host.runbookText.mockReturnValue(of({ runbook: 'x', markdown: ['1. drain', '   - check `a`', '   - check `b`', '2. restore'].join('\n') }));
    const fixture = render();
    open(fixture, 'outbox-growth');

    const outer = el(fixture).querySelectorAll('.runbook-text > ul');
    expect(outer).toHaveLength(1);
    expect(outer[0].querySelectorAll(':scope > li')).toHaveLength(2);
    expect(Array.from(outer[0].querySelectorAll(':scope > li > ul > li')).map((li) => li.textContent?.trim())).toEqual(['check a', 'check b']);
  });

  it("shows the alert's state beside the runbook rendered from the clone, and reads the alerts again on the poll", () => {
    const fixture = render();
    open(fixture, 'outbox-growth');

    expect(host.runbookText).toHaveBeenCalledWith('outbox-growth');
    expect(el(fixture).querySelector('td.state')?.textContent?.trim()).toBe('[inactive]');
    expect(el(fixture).querySelector('.runbook-text h3')?.textContent).toBe('Runbook — x');
    expect(el(fixture).querySelector('.runbook-text code')?.textContent).toBe('step one');

    host.telemetryAlerts.mockReturnValue(of(strip('firing')));
    vi.advanceTimersByTime(ALERT_POLL_MS);
    fixture.detectChanges();

    expect(el(fixture).querySelector('td.state')?.textContent?.trim()).toBe('[firing]');
    expect(el(fixture).querySelector('tr.firing')).not.toBeNull();
  });

  it('says an alert is not loaded where Prometheus does not list its rule, and unknown where Grafana did not answer', () => {
    const fixture = render();
    open(fixture, 'contact-refused');

    expect(el(fixture).querySelector('td.state')?.textContent?.trim()).toBe('[not loaded]');

    host.telemetryAlerts.mockReturnValue(of({ reachable: false, error: 'refused', loaded: [], waiting: [] }));
    vi.advanceTimersByTime(ALERT_POLL_MS);
    fixture.detectChanges();

    expect(el(fixture).querySelector('td.state')?.textContent?.trim()).toBe('[unknown]');
    expect(el(fixture).textContent).toContain('Grafana did not answer');
  });

  it('causes a runbook that costs nothing with one press and no confirmation', () => {
    const fixture = render();
    open(fixture, 'error-queue');

    button(fixture, 'button.cause').click();
    fixture.detectChanges();

    expect(host.runbookCause).toHaveBeenCalledWith('error-queue', null, null);
    expect(el(fixture).querySelector('app-output-pane')).not.toBeNull();
  });

  it('arms the Cause button on the first press of a confirm phase and sends the word on the second', () => {
    const fixture = render();
    open(fixture, 'outbox-growth');

    button(fixture, 'button.cause').click();
    fixture.detectChanges();

    expect(host.runbookCause).not.toHaveBeenCalled();
    expect(button(fixture, 'button.cause').textContent).toContain('Confirm: cause OutboxGrowth (outage)');

    button(fixture, 'button.cause').click();
    fixture.detectChanges();

    expect(host.runbookCause).toHaveBeenCalledWith('outbox-growth', null, 'cause');
  });

  it("keeps a typed phase disabled until the runbook's name is typed, as a wipe is", () => {
    const fixture = render();
    open(fixture, 'contact-refused');

    expect(button(fixture, 'button.cause').disabled).toBe(true);

    const input = el(fixture).querySelector('input') as HTMLInputElement;
    input.value = 'contact-refus';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(button(fixture, 'button.cause').disabled).toBe(true);

    input.value = 'contact-refused';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(button(fixture, 'button.cause').disabled).toBe(false);

    button(fixture, 'button.cause').click();
    expect(host.runbookCause).toHaveBeenCalledWith('contact-refused', null, 'contact-refused');
  });

  it("names a second phase's alert and sends none for the first", () => {
    const fixture = render();
    open(fixture, 'error-rate');
    const select = el(fixture).querySelector('select') as HTMLSelectElement;
    select.value = 'ErrorRateService';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    button(fixture, 'button.cause').click();
    fixture.detectChanges();
    button(fixture, 'button.cause').click();

    expect(host.runbookCause).toHaveBeenCalledWith('error-rate', 'ErrorRateService', 'cause');
  });

  it('undoes with no confirmation, and says the refusal when the host answers one', () => {
    const fixture = render();
    open(fixture, 'outbox-growth');

    button(fixture, 'button.undo').click();
    expect(host.runbookRestore).toHaveBeenCalledWith('outbox-growth');

    host.runbookRestore.mockReturnValue(throwError(() => ({ error: { detail: 'A game day cause outbox-growth is still running' } })));
    fixture.componentInstance.jobEnded();
    fixture.detectChanges();
    button(fixture, 'button.undo').click();
    fixture.detectChanges();

    expect(el(fixture).querySelector('p.error')?.textContent).toContain('still running');
  });

  it("keeps a running job's output, named for its runbook, when another runbook is chosen", () => {
    const fixture = render();
    open(fixture, 'error-queue');
    button(fixture, 'button.cause').click();
    fixture.detectChanges();

    open(fixture, 'stuck-saga');

    expect(el(fixture).querySelector('section.job')?.textContent).toContain('cause of ErrorQueueDepth (error-queue)');
    expect(el(fixture).querySelector('app-output-pane')).not.toBeNull();

    fixture.componentInstance.jobEnded();
    open(fixture, 'error-queue');
    expect(button(fixture, 'button.cause').disabled).toBe(false);
  });

  it('offers a runbook with no script nothing to press, and says why', () => {
    const fixture = render();
    open(fixture, 'stuck-saga');

    expect(el(fixture).querySelector('button.cause')).toBeNull();
    expect(el(fixture).querySelector('.no-script')?.textContent).toContain('its rule is not loaded');
  });

  it('says the runbook could not be read where the host has no text for it', () => {
    host.runbookText.mockReturnValue(throwError(() => ({ error: { detail: 'not a file in docs/runbooks' } })));
    const fixture = render();
    open(fixture, 'stuck-saga');

    expect(el(fixture).querySelector('.runbook-text [role="alert"]')?.textContent).toContain('not a file in docs/runbooks');
  });
});
