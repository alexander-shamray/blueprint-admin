import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { AlertStripView, JobSummary, RunbookCatalogue } from '../../../core/host/host-types';
import { SseClient } from '../../../core/host/sse-client';
import { WIDGET_CONTEXT } from '../../../workspace/widget-context';
import { RunbooksWidget } from './runbooks-widget';

const catalogue: RunbookCatalogue = {
  error: null,
  runbooks: [
    {
      runbook: 'error-queue',
      alerts: ['ErrorQueueDepth'],
      script: true,
      reason: null,
      phases: [
        {
          alert: 'ErrorQueueDepth',
          impact: 'none',
          confirmation: 'none',
          confirmWith: null,
          sustained: false,
          firesWithinSeconds: 240,
          clearsWithinSeconds: 240,
        },
      ],
    },
    {
      runbook: 'outbox-growth',
      alerts: ['OutboxGrowth', 'OutboxStuck'],
      script: true,
      reason: null,
      phases: [
        {
          alert: 'OutboxGrowth',
          impact: 'stops the broker',
          confirmation: 'confirm',
          confirmWith: null,
          sustained: true,
          firesWithinSeconds: 300,
          clearsWithinSeconds: 300,
        },
        {
          alert: 'OutboxStuck',
          impact: 'takes the stack down',
          confirmation: 'down -v',
          confirmWith: 'down -v',
          sustained: false,
          firesWithinSeconds: 300,
          clearsWithinSeconds: 300,
        },
      ],
    },
  ],
};

const strip: AlertStripView = { reachable: true, error: null, loaded: [], waiting: [] };
const job: JobSummary = { id: 'job-1', commandLine: 'game-day', state: 'Running', exitCode: null, startedAt: '' };

describe('RunbooksWidget', () => {
  let host: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    vi.useFakeTimers();
    host = {
      runbooks: vi.fn(() => of(catalogue)),
      runbookText: vi.fn(() => of({ runbook: 'x', markdown: '# Runbook\n\nStep.' })),
      runbookCause: vi.fn(() => of(job)),
      runbookRestore: vi.fn(() => of(job)),
      telemetryAlerts: vi.fn(() => of(strip)),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => of() } },
        // As the grid tells a widget on page `p`; leaving the page destroys it, and coming back creates it again.
        { provide: WIDGET_CONTEXT, useValue: { instanceId: 'runbooks', pageId: 'p' } },
      ],
    });
  });

  afterEach(() => vi.useRealTimers());

  function render(config: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(RunbooksWidget);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    vi.advanceTimersByTime(0);
    fixture.detectChanges();
    return fixture;
  }

  function open(fixture: ReturnType<typeof render>, name: string): void {
    const button = (Array.from(fixture.nativeElement.querySelectorAll('button.runbook')) as HTMLButtonElement[]).find(
      (b) => b.textContent?.includes(name),
    );
    button!.click();
    fixture.detectChanges();
  }

  it('opens on the runbook its config names, and again when the config names another', () => {
    const fixture = render({ runbook: 'error-queue' });
    expect(fixture.componentInstance.selected()).toBe('error-queue');
    expect(host['runbookText']).toHaveBeenCalledWith('error-queue');

    fixture.componentRef.setInput('config', { runbook: 'outbox-growth' });
    fixture.detectChanges();
    expect(fixture.componentInstance.selected()).toBe('outbox-growth');
  });

  it('keeps the open runbook, the alert to cause and the job shown for the tab, and reads the text again', () => {
    const first = render();
    open(first, 'outbox-growth');
    first.componentInstance.choosePhase('OutboxStuck');
    first.componentInstance.restore();
    first.destroy();
    host['runbookText'].mockClear();

    const again = render();

    expect(again.componentInstance.selected()).toBe('outbox-growth');
    expect(again.componentInstance.phaseAlert()).toBe('OutboxStuck');
    expect(again.componentInstance.jobId()).toBe('job-1');
    expect(again.componentInstance.jobLabel()).toBe('undo of outbox-growth');
    expect(host['runbookText']).toHaveBeenCalledWith('outbox-growth');
    expect(again.nativeElement.querySelector('app-output-pane')).not.toBeNull();
  });

  it('takes a job found on return to be running until its output says it ended, so no cause starts on top of it', () => {
    const first = render();
    open(first, 'error-queue');
    first.componentInstance.restore();
    first.destroy();

    const again = render();
    expect(again.componentInstance.running()).toBe(true);
    expect((again.nativeElement.querySelector('button.undo') as HTMLButtonElement).disabled).toBe(true);

    again.componentInstance.jobEnded();
    again.detectChanges();
    expect((again.nativeElement.querySelector('button.undo') as HTMLButtonElement).disabled).toBe(false);
  });

  describe('countdowns', () => {
    let seq = 0;
    const said = (text: string) => ({ sequence: ++seq, at: new Date().toISOString(), stream: 'Stdout' as const, text });
    const firing: AlertStripView = {
      reachable: true,
      error: null,
      loaded: [{ name: 'OutboxGrowth', state: 'firing' } as AlertStripView['loaded'][number]],
      waiting: [],
    };
    const labels = (fixture: ReturnType<typeof render>): string[] =>
      Array.from(fixture.nativeElement.querySelectorAll('.countdowns li') as NodeListOf<HTMLElement>).map((li) =>
        (li.textContent ?? '').trim(),
      );

    it('counts down the phase’s window from the tool’s causing line, in the listing’s own figures', () => {
      const fixture = render();
      fixture.componentInstance.read(said('outbox-growth: causing OutboxGrowth; it fires within 999s once caused'));
      fixture.detectChanges();
      expect(fixture.componentInstance.countdowns()[0].windowSeconds).toBe(300);

      vi.advanceTimersByTime(60_000);
      fixture.detectChanges();

      expect(fixture.componentInstance.countdowns()[0].remainingSeconds).toBe(240);
      expect(fixture.componentInstance.countdowns()[0].status).toBe('waiting');
    });

    it('turns to fired once the strip reads the alert firing, and stays so when it resolves again', () => {
      const fixture = render();
      fixture.componentInstance.read(said('outbox-growth: causing OutboxGrowth; it fires within 300s once caused'));
      fixture.detectChanges();
      host['telemetryAlerts'].mockReturnValue(of(firing));

      vi.advanceTimersByTime(5000);
      fixture.detectChanges();
      expect(fixture.componentInstance.countdowns()[0].status).toBe('settled');

      host['telemetryAlerts'].mockReturnValue(of(strip));
      vi.advanceTimersByTime(5000);
      expect(fixture.componentInstance.countdowns()[0].status).toBe('settled');
    });

    it('turns overdue when the window passes with the alert still quiet', () => {
      const fixture = render();
      fixture.componentInstance.read(said('outbox-growth: causing OutboxGrowth; it fires within 300s once caused'));
      fixture.detectChanges();

      vi.advanceTimersByTime(301_000);

      expect(fixture.componentInstance.countdowns()[0].status).toBe('overdue');
    });

    it('waits on a restore’s alert while the line says it still fires, and none when it says it is quiet', () => {
      const fixture = render();
      fixture.componentInstance.read(
        said('OutboxGrowth is firing after the restore; its window clears it within 300s'),
      );
      fixture.componentInstance.read(said('OutboxStuck is inactive after the restore'));

      const [growth, stuck] = fixture.componentInstance.countdowns();

      expect(growth.status).toBe('waiting');
      expect(stuck.status).toBe('settled');
    });

    it('draws the rows above the log once the pane hands over the lines it claimed', () => {
      TestBed.overrideProvider(SseClient, {
        useValue: {
          follow: () =>
            of({
              kind: 'line',
              line: said('outbox-growth: causing OutboxGrowth; it fires within 300s once caused'),
            }),
        },
      });
      const first = render();
      first.componentInstance.jobId.set('job-1');
      first.detectChanges();

      expect(labels(first)).toEqual([expect.stringContaining('OutboxGrowth should fire within 5 min')]);
    });
  });

  describe('confirmations', () => {
    it('start disarmed on return: the first press of a confirm cause is not kept', () => {
      const first = render();
      open(first, 'outbox-growth');
      (first.nativeElement.querySelector('button.cause') as HTMLButtonElement).click();
      first.detectChanges();
      expect(first.componentInstance.armed()).toBe(true);
      first.destroy();

      const again = render();

      expect(again.componentInstance.selected()).toBe('outbox-growth');
      expect(again.componentInstance.armed()).toBe(false);
      (again.nativeElement.querySelector('button.cause') as HTMLButtonElement).click();
      expect(host['runbookCause']).not.toHaveBeenCalled();
    });

    it('start empty on return: the typed name of a costly cause is not kept', () => {
      const first = render();
      open(first, 'outbox-growth');
      first.componentInstance.choosePhase('OutboxStuck');
      first.componentInstance.confirmText.set('down -v');
      first.detectChanges();
      expect(first.componentInstance.canCause()).toBe(true);
      first.destroy();

      const again = render();

      expect(again.componentInstance.phaseAlert()).toBe('OutboxStuck');
      expect(again.componentInstance.confirmText()).toBe('');
      expect(again.componentInstance.canCause()).toBe(false);
    });
  });
});
