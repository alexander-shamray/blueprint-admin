import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { DeadLetterInspection, JobSummary } from '../../../core/host/host-types';
import { SseClient } from '../../../core/host/sse-client';
import { BrokerFeed } from '../broker-feed';
import { brokerHost, inspection, job } from '../broker-testing';
import { DeadLetterWidget } from './dead-letter-widget';

describe('DeadLetterWidget', () => {
  let host: ReturnType<typeof brokerHost>;

  beforeEach(() => {
    host = brokerHost();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => of() } },
      ],
    });
  });

  type Fixture = ReturnType<typeof render>;

  function render(config: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(DeadLetterWidget);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    return fixture;
  }

  /** What a Queues widget's Open button does. */
  function open(fixture: Fixture, queue = 'ordering-catalog-events_error'): void {
    TestBed.inject(BrokerFeed).requestOpen(queue);
    fixture.detectChanges();
  }

  function click(fixture: Fixture, selector: string): void {
    (fixture.nativeElement.querySelector(selector) as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  function type(fixture: Fixture, value: string): void {
    const input = fixture.nativeElement.querySelector('.execute-box input') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('counts itself as a workbench a Queues widget can open into, while it exists', () => {
    const feed = TestBed.inject(BrokerFeed);
    const fixture = render();
    expect(feed.workbenches()).toBe(1);

    fixture.destroy();
    expect(feed.workbenches()).toBe(0);
  });

  it('starts closed, with nothing read, and says how to open a queue', () => {
    const fixture = render();

    expect(host.deadLetters).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('press Open on a parked queue');
  });

  it('opens the queue it is configured for only on the operator pressing Open', () => {
    const fixture = render({ queue: 'ordering-catalog-events_error' });
    expect(host.deadLetters).not.toHaveBeenCalled();

    click(fixture, 'button.open-configured');

    expect(host.deadLetters).toHaveBeenCalledWith('ordering-catalog-events_error');
  });

  it('opens a queue into its messages, the correlation id a link into Trace', () => {
    const fixture = render();
    open(fixture);

    expect(host.deadLetters).toHaveBeenCalledWith('ordering-catalog-events_error');
    const articles = Array.from(fixture.nativeElement.querySelectorAll('article.dead-letter')) as HTMLElement[];
    expect(articles.length).toBe(2);
    expect(articles[0].textContent).toContain('System.InvalidOperationException: no row in the price projection');
    expect(articles[0].querySelector('a')?.getAttribute('href')).toBe('/trace/corr-1');
    expect(articles[1].textContent).toContain('(no message id)');
    expect(articles[1].querySelector('button.dry-replay')).toBeNull();
  });

  it('says the reason the tool gave when it read nothing', () => {
    host.deadLetters.mockReturnValue(
      of({ ...inspection, messages: [], error: 'dead_letters: the broker refused the credential' }),
    );
    const fixture = render();
    open(fixture);

    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('the broker refused the credential');
  });

  it('says how many messages a read shows, so a deeper queue is not taken as all of it', () => {
    const fixture = render();
    open(fixture);

    expect(fixture.nativeElement.querySelector('.dead-letters p')?.textContent).toContain('The first 20 messages');
  });

  it('offers the real run only after a dry run, and only with the typed confirmation', () => {
    const fixture = render();
    open(fixture);

    expect(fixture.nativeElement.querySelector('button.execute')).toBeNull();
    click(fixture, 'button.dry-replay');
    expect(host.deadLetterMove).toHaveBeenCalledWith('ordering-catalog-events_error', 'replay', ['m-1'], null);
    expect(fixture.nativeElement.querySelector('app-output-pane')).not.toBeNull();

    const execute = () => fixture.nativeElement.querySelector('button.execute') as HTMLButtonElement;
    expect(execute().textContent?.trim()).toBe('Replay for real');
    expect(execute().disabled).toBe(true);
    type(fixture, '--execut');
    expect(execute().disabled).toBe(true);
    type(fixture, '--execute');
    expect(execute().disabled).toBe(false);

    click(fixture, 'button.execute');
    expect(host.deadLetterMove).toHaveBeenLastCalledWith(
      'ordering-catalog-events_error',
      'replay',
      ['m-1'],
      '--execute',
    );
    expect(fixture.nativeElement.querySelector('button.execute')).toBeNull();
  });

  it('drops what it listed once the real run is sent, reads the queues, and reads the queue again on asking', () => {
    const fixture = render();
    open(fixture);
    click(fixture, 'button.dry-replay');
    type(fixture, '--execute');
    host.brokerQueues.mockClear();
    click(fixture, 'button.execute');

    expect(fixture.nativeElement.querySelectorAll('article.dead-letter').length).toBe(0);
    expect(fixture.nativeElement.querySelector('.moved')?.textContent).toContain('m-1 went to the real run');
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
    click(fixture, 'button.reread');
    expect(host.deadLetters).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.querySelectorAll('article.dead-letter').length).toBe(2);
    expect(fixture.nativeElement.querySelector('.moved')).toBeNull();
  });

  it('tabulates the tool’s per-message lines above the log, the count line saying a dry run', () => {
    const line = (sequence: number, text: string) => ({
      kind: 'line' as const,
      line: { sequence, at: '2026-10-28T10:00:00Z', stream: 'Stdout' as const, text },
    });
    (TestBed.inject(SseClient) as { follow: unknown }).follow = () =>
      of(
        line(1, 'would-replay    m-1 -> rabbitmq://rabbitmq/ordering-catalog-events'),
        line(2, 'not found       m-9'),
        line(3, 'dry run: nothing left ordering-catalog-events_error.'),
      );
    const fixture = render();
    open(fixture);
    click(fixture, 'button.dry-replay');
    // The pane follows the job in an effect, which runs on that pass; its lines land after it.
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.move-summary')?.textContent).toBe('1 would be replayed, 1 not found (dry run)');
    const rows = Array.from(el.querySelectorAll('.move-table tbody tr'));
    expect(rows.map((r) => r.querySelector('th')?.textContent)).toEqual(['m-1', 'm-9']);
    expect(rows[1].className).toBe('not-found');
    expect(el.querySelector('.move-hint')).not.toBeNull();
    // The tool's own closing line stays in the log; the two result lines wait behind the toggle.
    expect(el.querySelector('pre.pane')?.textContent).toContain('dry run: nothing left');
    expect(el.querySelector('pre.pane')?.textContent).not.toContain('would-replay');
    click(fixture, 'button.pull-lines');
    expect(el.querySelector('pre.pane')?.textContent).toContain('would-replay');
  });

  it("opening another queue does not show the last queue's job", () => {
    const fixture = render();
    open(fixture);
    click(fixture, 'button.dry-replay');
    expect(fixture.nativeElement.querySelector('app-output-pane')).not.toBeNull();

    open(fixture, 'bff-order-events_skipped');

    expect(fixture.nativeElement.querySelector('app-output-pane')).toBeNull();
  });

  it('a dry run of the other verb re-arms the real run for that verb', () => {
    const fixture = render();
    open(fixture);
    click(fixture, 'button.dry-replay');
    click(fixture, 'button.dry-discard');

    expect((fixture.nativeElement.querySelector('button.execute') as HTMLButtonElement).textContent?.trim()).toBe(
      'Discard for real',
    );
  });

  it('a dry run that lands after the listing was closed or replaced arms nothing', () => {
    const late = new Subject<JobSummary>();
    host.deadLetterMove.mockReturnValueOnce(late.asObservable());
    const fixture = render();
    open(fixture);
    click(fixture, 'button.dry-replay');

    click(fixture, 'button.close-dead-letters');
    open(fixture, 'bff-order-events_skipped');
    late.next(job);
    late.complete();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('button.execute')).toBeNull();
    expect(fixture.nativeElement.querySelector('app-output-pane')).toBeNull();
  });

  it('a real run answered after the listing was closed reads the queues but shows its job nowhere', () => {
    const late = new Subject<JobSummary>();
    const fixture = render();
    open(fixture);
    click(fixture, 'button.dry-replay');
    type(fixture, '--execute');
    host.deadLetterMove.mockReturnValueOnce(late.asObservable());
    host.brokerQueues.mockClear();
    click(fixture, 'button.execute');

    click(fixture, 'button.close-dead-letters');
    late.next(job);
    late.complete();
    fixture.detectChanges();

    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelector('.moved')).toBeNull();
    expect(fixture.nativeElement.querySelector('app-output-pane')).toBeNull();
  });

  it('keeps the latest request made while a queue is being read, and opens it when that read ends', () => {
    const first = new Subject<DeadLetterInspection>();
    host.deadLetters.mockReturnValueOnce(first.asObservable());
    const fixture = render();
    open(fixture, 'ordering-catalog-events_error');

    open(fixture, 'bff-order-events_skipped');
    expect(host.deadLetters).toHaveBeenCalledTimes(1);
    first.next(inspection);
    first.complete();
    fixture.detectChanges();

    expect(host.deadLetters).toHaveBeenLastCalledWith('bff-order-events_skipped');
    expect(fixture.nativeElement.querySelector('.dead-letters code')?.textContent).toBe('bff-order-events_skipped');
  });

  it('says why the host refused a move, and arms nothing', () => {
    host.deadLetterMove.mockReturnValue(
      throwError(() => ({ error: { detail: "The operator's password is set for one run at a time." } })),
    );
    const fixture = render();
    open(fixture);
    click(fixture, 'button.dry-replay');

    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('one run at a time');
    expect(fixture.nativeElement.querySelector('button.execute')).toBeNull();
  });

  it('offers nothing to open into when the host did not give the plan', () => {
    host.deadLetterPlan.mockReturnValue(throwError(() => new Error('down')));
    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain("did not give the dead-letter tool's plan");
  });

  it('is re-created closed, with the dry-run gate and the typed confirmation both empty', () => {
    const first = render();
    open(first);
    click(first, 'button.dry-replay');
    type(first, '--execute');
    expect((first.nativeElement.querySelector('button.execute') as HTMLButtonElement).disabled).toBe(false);
    first.destroy();

    const again = render();

    // The request that opened the first is not replayed into the new widget.
    expect(host.deadLetters).toHaveBeenCalledTimes(1);
    expect(again.nativeElement.querySelector('article.dead-letter')).toBeNull();
    expect(again.nativeElement.querySelector('button.execute')).toBeNull();
    expect(again.nativeElement.querySelector('.execute-box')).toBeNull();
  });
});
