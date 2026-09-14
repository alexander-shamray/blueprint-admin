import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { JobEvent, SseClient } from '../../core/host/sse-client';
import { LogsPage } from './logs-page';

describe('LogsPage', () => {
  let events: Subject<JobEvent>;
  let host: { followLogs: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    events = new Subject<JobEvent>();
    host = { followLogs: vi.fn(() => of({ id: 'logs-1', commandLine: 'docker compose logs -f', state: 'Running', exitCode: null, startedAt: '' })) };
    TestBed.configureTestingModule({
      imports: [LogsPage],
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => events.asObservable() } },
      ],
    });
  });

  function line(sequence: number, text: string): JobEvent {
    return { kind: 'line', line: { sequence, at: '', stream: 'Stdout', text } };
  }

  it('follows the selected services and shows lines', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.selected.set(['gateway']);
    fixture.componentInstance.follow();
    events.next(line(0, 'gateway | started'));
    fixture.detectChanges();

    expect(host.followLogs).toHaveBeenCalledWith(['gateway']);
    expect(fixture.nativeElement.textContent).toContain('gateway | started');
  });

  it('filters by text and highlights the correlation id', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    events.next(line(0, 'gateway | proxying CorrelationId=abc'));
    events.next(line(1, 'catalog-api | listed products'));
    fixture.componentInstance.filter.set('gateway');
    fixture.componentInstance.correlationId.set('abc');
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('.line')) as HTMLElement[];
    expect(rows.length).toBe(1);
    expect(rows[0].classList.contains('hit')).toBe(true);
  });

  it('shows the problem detail when the follow request fails and does not go live', () => {
    host.followLogs.mockReturnValueOnce(
      throwError(() => ({ error: { title: 'Backend unreachable', detail: 'Docker did not answer.' } })),
    );

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Docker did not answer.');
    expect(fixture.nativeElement.querySelector('.live')).toBeNull();
    expect(fixture.componentInstance.following()).toBe(false);
  });
});
