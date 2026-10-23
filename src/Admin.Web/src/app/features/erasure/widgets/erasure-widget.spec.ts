import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { ErasureRequestView, HoldersView, ProxyResult } from '../../../core/host/host-types';
import { WIDGET_CONTEXT } from '../../../workspace/widget-context';
import { ErasureRun, PRIVACY_IDENTITY } from '../erasure-run';
import { ErasureWidget } from './erasure-widget';

const holders: HoldersView = {
  reachable: true,
  error: null,
  holders: [
    { name: 'ordering', queue: 'ordering-privacy', pending: 0 },
    { name: 'payments', queue: 'payments-privacy', pending: 2 },
  ],
  completionsPending: 1,
  runbookUrl: 'https://example.test/runbooks/erasure-overdue.md',
};

const REQUEST_ID = '0199a1b2-0000-7000-8000-000000000300';
const SUBJECT = '11111111-2222-4333-8444-555555555555';

function view(over: Partial<ErasureRequestView>): ErasureRequestView {
  return {
    requestId: REQUEST_ID,
    status: 'Open',
    raisedAt: '2026-10-15T12:00:00Z',
    dueAt: '2026-11-14T12:00:00Z',
    closedAt: null,
    reissues: 0,
    responders: ['ordering', 'payments'],
    missing: ['payments'],
    answers: [{ responder: 'ordering', count: 1, counted: true, receivedAt: '2026-10-15T12:00:01Z' }],
    ...over,
  };
}

function responded(status: number, body: string): ProxyResult {
  return { outcome: 'responded', status, headers: {}, body, bodyTruncated: false, bodyError: null, elapsedMs: 3, correlationId: 'corr-1' };
}

describe('ErasureWidget', () => {
  let proxy: ReturnType<typeof vi.fn>;

  function render(answers: ProxyResult[]) {
    proxy = vi.fn();
    for (const a of answers) proxy.mockReturnValueOnce(of(a));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: HostClient,
          useValue: { config: () => of({ urls: { gateway: 'http://gw.test/' } }), erasureHolders: () => of(holders), proxy },
        },
      ],
    });
    const fixture = TestBed.createComponent(ErasureWidget);
    fixture.detectChanges();
    return fixture;
  }

  function text(fixture: { nativeElement: HTMLElement }, selector: string): string[] {
    return (Array.from(fixture.nativeElement.querySelectorAll(selector)) as HTMLElement[]).map((e) => e.textContent?.replace(/\s+/g, ' ').trim() ?? '');
  }

  afterEach(() => vi.useRealTimers());

  it('lists the backend holders with their queue depths before any request exists', () => {
    const fixture = render([]);

    expect(text(fixture, 'table.holders tbody tr')).toEqual(['ordering [unasked] ordering-privacy 0', 'payments [unasked] payments-privacy 2']);
    expect(fixture.nativeElement.querySelector('.completions').textContent).toContain('1');
  });

  it('raises as the privacy login through the gateway, then reads the request it was given', () => {
    const fixture = render([responded(200, JSON.stringify(REQUEST_ID)), responded(200, JSON.stringify(view({})))]);
    fixture.componentInstance.subject.set(SUBJECT);
    fixture.componentInstance.raise();
    fixture.detectChanges();

    expect(proxy).toHaveBeenNthCalledWith(1, {
      method: 'POST',
      url: 'http://gw.test/api/v1/privacy/erasure-requests',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ subjectId: SUBJECT }),
      identity: PRIVACY_IDENTITY,
      correlationId: null,
    });
    expect(proxy.mock.calls[1][0]).toMatchObject({ method: 'GET', url: `http://gw.test/api/v1/privacy/erasure-requests/${REQUEST_ID}`, body: null });
    expect(text(fixture, 'table.holders tbody tr')).toEqual(['ordering [answered] 1 · 2026-10-15T12:00:01Z ordering-privacy 0', 'payments [waiting] payments-privacy 2']);
    expect(fixture.nativeElement.querySelector('.status').textContent).toContain('[open]');
    expect(fixture.nativeElement.querySelector('button.reissue')).toBeNull();
  });

  it('stays busy until the read a raise makes has answered, so Raise cannot be pressed twice in between', () => {
    const read = new Subject<ProxyResult>();
    const fixture = render([]);
    proxy.mockReturnValueOnce(of(responded(200, JSON.stringify(REQUEST_ID)))).mockReturnValueOnce(read);
    fixture.componentInstance.subject.set(SUBJECT);
    fixture.componentInstance.raise();

    expect(proxy).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.run.busy()).toBe(true);
    expect(fixture.componentInstance.canRaise()).toBe(false);

    read.next(responded(200, JSON.stringify(view({}))));
    read.complete();

    expect(fixture.componentInstance.run.busy()).toBe(false);
    expect(fixture.componentInstance.canRaise()).toBe(true);
  });

  it('refuses a subject that is not a GUID before anything is sent', () => {
    const fixture = render([]);
    fixture.componentInstance.subject.set('not-a-guid');
    fixture.componentInstance.raise();

    expect(fixture.componentInstance.canRaise()).toBe(false);
    expect(proxy).not.toHaveBeenCalled();
  });

  it('shows an overdue holder, offers the reissue with the runbook, and reads again after it', () => {
    const overdue = view({ status: 'Overdue', missing: ['payments'], reissues: 0 });
    const closed = view({
      status: 'Closed',
      missing: [],
      closedAt: '2026-10-15T12:05:00Z',
      reissues: 1,
      answers: [
        { responder: 'ordering', count: 1, counted: true, receivedAt: '2026-10-15T12:00:01Z' },
        { responder: 'payments', count: 1, counted: true, receivedAt: '2026-10-15T12:05:00Z' },
      ],
    });
    const fixture = render([responded(200, JSON.stringify(REQUEST_ID)), responded(200, JSON.stringify(overdue)), responded(204, ''), responded(200, JSON.stringify(closed))]);
    fixture.componentInstance.subject.set(SUBJECT);
    fixture.componentInstance.raise();
    fixture.detectChanges();

    expect(text(fixture, 'table.holders tbody tr')[1]).toBe('payments [overdue] payments-privacy 2');
    expect(fixture.nativeElement.querySelector('.overdue-actions a').getAttribute('href')).toBe('https://example.test/runbooks/erasure-overdue.md');

    (fixture.nativeElement.querySelector('button.reissue') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(proxy.mock.calls[2][0]).toMatchObject({ method: 'POST', url: `http://gw.test/api/v1/privacy/erasure-requests/${REQUEST_ID}/reissue` });
    expect(JSON.parse(proxy.mock.calls[2][0].body as string)).toEqual({ commandId: expect.stringMatching(/^[0-9a-f-]{36}$/) });
    expect(fixture.nativeElement.querySelector('.status').textContent).toContain('[closed]');
    expect(fixture.nativeElement.querySelector('button.reissue')).toBeNull();
  });

  it('says what the gateway answered when the login lacks the role, and does not read', () => {
    const fixture = render([responded(403, '{"title":"Forbidden"}')]);
    fixture.componentInstance.subject.set(SUBJECT);
    fixture.componentInstance.raise();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.error').textContent).toContain('the gateway answered 403');
    expect(proxy).toHaveBeenCalledTimes(1);
  });

  it('names a token the realm refused, which is a realm imported before the login existed', () => {
    const fixture = render([{ outcome: 'tokenRejected', status: 401, body: 'invalid_grant', correlationId: 'c' }]);
    fixture.componentInstance.subject.set(SUBJECT);
    fixture.componentInstance.raise();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.error').textContent).toContain('docker compose down -v');
  });

  it('shows an answer from outside the set the request was raised with, marked not counted', () => {
    const stray = view({
      status: 'Closed',
      missing: [],
      responders: ['ordering'],
      answers: [
        { responder: 'ordering', count: 1, counted: true, receivedAt: 't1' },
        { responder: 'legacy', count: 1, counted: false, receivedAt: 't2' },
      ],
    });
    const fixture = render([responded(200, JSON.stringify(stray))]);
    fixture.componentInstance.run.requestId.set(REQUEST_ID);
    fixture.componentInstance.run.read();
    fixture.detectChanges();

    expect(text(fixture, 'table.holders tbody tr')[1]).toContain('legacy [answered] not counted');
  });

  it('reads every 3 seconds while the watch is on, and stops by itself once the request has closed', async () => {
    vi.useFakeTimers();
    const closed = view({ status: 'Closed', missing: [], closedAt: 't', answers: [] });
    const fixture = render([responded(200, JSON.stringify(view({}))), responded(200, JSON.stringify(closed))]);
    fixture.componentInstance.run.requestId.set(REQUEST_ID);
    fixture.componentInstance.run.setWatch(true);
    fixture.detectChanges();

    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(3000);
    await vi.advanceTimersByTimeAsync(30_000);

    expect(proxy).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.run.watch()).toBe(false);
  });

  it('a watch tick does not disable Raise or clear the error shown, and waits for a call of the operator\'s', async () => {
    vi.useFakeTimers();
    const tick = new Subject<ProxyResult>();
    const fixture = render([]);
    proxy.mockReturnValueOnce(tick);
    const run = TestBed.inject(ErasureRun);
    run.requestId.set(REQUEST_ID);
    run.error.set('an earlier failure');
    run.setWatch(true);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);

    expect(proxy).toHaveBeenCalledTimes(1);
    expect(run.busy()).toBe(false);
    expect(run.error()).toBe('an earlier failure');

    // An operator's call is out: the next tick reads nothing.
    const call = new Subject<ProxyResult>();
    proxy.mockReturnValueOnce(call);
    run.read();
    await vi.advanceTimersByTimeAsync(30_000);
    expect(proxy).toHaveBeenCalledTimes(2);
    expect(run.busy()).toBe(true);
    tick.complete();
    call.complete();
  });

  it('reads nothing once the widget is gone, though the watch stays on for the next one', async () => {
    vi.useFakeTimers();
    const fixture = render([responded(200, JSON.stringify(view({})))]);
    proxy.mockReturnValue(of(responded(200, JSON.stringify(view({})))));
    const run = TestBed.inject(ErasureRun);
    run.requestId.set(REQUEST_ID);
    run.setWatch(true);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    const reads = proxy.mock.calls.length;

    fixture.destroy();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(proxy.mock.calls.length).toBe(reads);
    expect(run.watch()).toBe(true);
  });

  it('keeps the request it watches when it is re-created, and the subject typed, as session state', async () => {
    const context = { provide: WIDGET_CONTEXT, useValue: { instanceId: 'w1', pageId: 'p1' } };
    TestBed.configureTestingModule({ providers: [context] });
    const first = render([responded(200, JSON.stringify(REQUEST_ID)), responded(200, JSON.stringify(view({})))]);
    first.componentInstance.subject.set(SUBJECT);
    first.componentInstance.raise();
    first.detectChanges();
    first.destroy();

    const again = TestBed.createComponent(ErasureWidget);
    again.detectChanges();
    await again.whenStable();
    again.detectChanges();

    expect((again.nativeElement.querySelector('input.subject') as HTMLInputElement).value).toBe(SUBJECT);
    expect(again.nativeElement.querySelector('.request code')?.textContent).toBe(REQUEST_ID);
  });
});
