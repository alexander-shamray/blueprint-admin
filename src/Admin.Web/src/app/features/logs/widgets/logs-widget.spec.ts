import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { JobSummary } from '../../../core/host/host-types';
import { JobEvent, SseClient } from '../../../core/host/sse-client';
import { WIDGET_CONTEXT } from '../../../workspace/widget-context';
import { LogFollowService } from '../log-follow';
import { LogsWidget } from './logs-widget';

describe('LogsWidget', () => {
  let events: Subject<JobEvent>;
  let host: { followLogs: ReturnType<typeof vi.fn>; stopFollow: ReturnType<typeof vi.fn> };

  function line(sequence: number, text: string, stream: 'Stdout' | 'Stderr' = 'Stdout'): JobEvent {
    return { kind: 'line', line: { sequence, at: '', stream, text } };
  }

  function render(services: string[]) {
    const fixture = TestBed.createComponent(LogsWidget);
    fixture.componentRef.setInput('config', { services });
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    events = new Subject<JobEvent>();
    const summary: JobSummary = { id: 'logs-1', commandLine: 'docker compose logs -f', state: 'Running', exitCode: null, startedAt: '' };
    host = { followLogs: vi.fn(() => of(summary)), stopFollow: vi.fn(() => of(undefined)) };
    TestBed.configureTestingModule({
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => events.asObservable() } },
      ],
    });
  });

  it('follows the services it is configured for and shows only their lines', () => {
    const fixture = render(['gateway']);
    (fixture.nativeElement.querySelector('button.follow') as HTMLButtonElement).click();
    events.next(line(0, 'gateway       | started'));
    events.next(line(1, 'catalog-api-1  | listed products'));
    fixture.detectChanges();

    expect(host.followLogs).toHaveBeenCalledWith(['gateway']);
    const rows = [...fixture.nativeElement.querySelectorAll('.line')].map((r) => (r as HTMLElement).textContent?.trim());
    expect(rows).toEqual(['gateway       | started']);
    expect(fixture.nativeElement.querySelector('.live')).not.toBeNull();
  });

  it('two widgets with different services each get their own lines from one follow', () => {
    const one = render(['gateway']);
    const two = render(['catalog-api']);
    (one.nativeElement.querySelector('button.follow') as HTMLButtonElement).click();
    events.next(line(0, 'gateway       | proxying'));
    events.next(line(1, 'catalog-api   | listed'));
    events.next(line(2, 'rabbitmq      | accepted'));
    one.detectChanges();
    two.detectChanges();

    expect(host.followLogs).toHaveBeenCalledTimes(1);
    expect(host.followLogs).toHaveBeenCalledWith(['catalog-api', 'gateway']);
    const texts = (f: typeof one) => [...f.nativeElement.querySelectorAll('.line')].map((r) => (r as HTMLElement).textContent?.trim());
    expect(texts(one)).toEqual(['gateway       | proxying']);
    expect(texts(two)).toEqual(['catalog-api   | listed']);
  });

  it('filters by text and marks correlation matches and stderr lines with text, not only colour', () => {
    const fixture = render([]);
    (fixture.nativeElement.querySelector('button.follow') as HTMLButtonElement).click();
    events.next(line(0, 'gateway | proxying CorrelationId=abc'));
    events.next(line(1, 'catalog-api | failed', 'Stderr'));
    events.next(line(2, 'catalog-api | listed products'));
    fixture.componentInstance.correlationId.set('abc');
    fixture.detectChanges();

    const rows = () => [...fixture.nativeElement.querySelectorAll('.line')].map((r) => (r as HTMLElement).textContent?.trim());
    expect(rows()).toEqual([
      '[match] gateway | proxying CorrelationId=abc',
      '[stderr] catalog-api | failed',
      'catalog-api | listed products',
    ]);

    fixture.componentInstance.filter.set('listed');
    fixture.detectChanges();
    expect(rows()).toEqual(['catalog-api | listed products']);
  });

  it('says which of its configured services Compose has none of', () => {
    const fixture = render(['gateway', 'gatway']);

    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('Not a Compose service: gatway');
    expect(render(['gateway']).nativeElement.querySelector('.error')).toBeNull();
  });

  it('enables Stop only while there is something to stop, and disables Follow while the request is out', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());
    const fixture = render([]);
    const stop = fixture.nativeElement.querySelector('button.stop') as HTMLButtonElement;
    const follow = fixture.nativeElement.querySelector('button.follow') as HTMLButtonElement;
    expect(stop.disabled).toBe(true);

    follow.click();
    fixture.detectChanges();

    expect(stop.disabled).toBe(false);
    expect(follow.disabled).toBe(true);
  });

  it('leaves the follow running when it is destroyed, and the service stops asking for its services', () => {
    const service = TestBed.inject(LogFollowService);
    const fixture = render(['gateway']);
    (fixture.nativeElement.querySelector('button.follow') as HTMLButtonElement).click();
    expect(service.services()).toEqual(['gateway']);

    fixture.destroy();

    expect(host.stopFollow).not.toHaveBeenCalled();
    expect(service.following()).toBe(true);
    expect(service.services()).toEqual([]);
  });

  it('keeps the text filter and the correlation id typed, for the tab, when it is re-created (session state, §4.2)', () => {
    TestBed.configureTestingModule({ providers: [{ provide: WIDGET_CONTEXT, useValue: { instanceId: 'logs', pageId: 'p' } }] });
    const first = render([]);
    first.componentInstance.filter.set('proxying');
    first.componentInstance.correlationId.set('fake-corr-0001');
    first.destroy();

    const again = render([]);

    expect(again.componentInstance.filter()).toBe('proxying');
    expect(again.componentInstance.correlationId()).toBe('fake-corr-0001');
  });

  it('a second widget created later keeps showing lines already received', () => {
    const first = render(['gateway']);
    (first.nativeElement.querySelector('button.follow') as HTMLButtonElement).click();
    events.next(line(0, 'gateway | started'));
    first.destroy();

    const again = render(['gateway']);

    expect(again.nativeElement.querySelector('.line')?.textContent?.trim()).toBe('gateway | started');
  });
});
