import { Component, DestroyRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, Observable, Subject, delay, of, throwError } from 'rxjs';
import { PolledFeed, TabVisibility } from './polled-feed';

describe('PolledFeed', () => {
  let read: ReturnType<typeof vi.fn<() => Observable<string>>>;
  let visible: BehaviorSubject<boolean>;
  let feed: PolledFeed<string>;

  /** A widget that reads the feed while it exists, as every Stack widget does. */
  @Component({ selector: 'app-reader', template: '' })
  class Reader {
    constructor() {
      feed.attach();
    }
  }

  beforeEach(() => {
    vi.useFakeTimers();
    read = vi.fn(() => of('answer'));
    visible = new BehaviorSubject(true);
    feed = new PolledFeed(read, 3000, visible, TestBed.inject(DestroyRef));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('polls nothing while no widget reads it: a page that is not showing has destroyed its widgets', async () => {
    await vi.advanceTimersByTimeAsync(60_000);

    expect(read).not.toHaveBeenCalled();
    expect(feed.value()).toBeUndefined();
  });

  it('reads at once for the first widget, every period after, and stops when the last one goes', async () => {
    const reader = TestBed.createComponent(Reader);
    await vi.advanceTimersByTimeAsync(0);
    expect(read).toHaveBeenCalledTimes(1);
    expect(feed.value()).toBe('answer');

    await vi.advanceTimersByTimeAsync(6000);
    expect(read).toHaveBeenCalledTimes(3);

    reader.destroy();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(read).toHaveBeenCalledTimes(3);
    // The last answer stays for the next widget to show while it reads again.
    expect(feed.value()).toBe('answer');
  });

  it('shares one poll between two widgets, and keeps it while either is left', async () => {
    const first = TestBed.createComponent(Reader);
    const second = TestBed.createComponent(Reader);
    await vi.advanceTimersByTimeAsync(3000);
    expect(read).toHaveBeenCalledTimes(2);

    first.destroy();
    await vi.advanceTimersByTimeAsync(3000);
    expect(read).toHaveBeenCalledTimes(3);

    second.destroy();
    await vi.advanceTimersByTimeAsync(3000);
    expect(read).toHaveBeenCalledTimes(3);
  });

  it('reads once on refresh with no widget attached, and not again while that read is out', async () => {
    const answer = new Subject<string>();
    read.mockReturnValueOnce(answer.asObservable());

    feed.refresh();
    feed.refresh();
    expect(read).toHaveBeenCalledTimes(1);
    expect(feed.refreshing()).toBe(true);

    answer.next('late');
    answer.complete();
    expect(feed.value()).toBe('late');
    expect(feed.refreshing()).toBe(false);

    feed.refresh();
    expect(read).toHaveBeenCalledTimes(2);
  });

  it('a failed refresh says why, keeps the last value and frees the next read', () => {
    feed.refresh();
    read.mockReturnValueOnce(throwError(() => new Error('host gone')));

    feed.refresh();

    expect(feed.error()).toBe('host gone');
    expect(feed.value()).toBe('answer');
    expect(feed.refreshing()).toBe(false);
  });

  it('skips a tick while a manual read is out', async () => {
    const answer = new Subject<string>();
    read.mockReturnValueOnce(answer.asObservable());
    feed.refresh();

    TestBed.createComponent(Reader);
    await vi.advanceTimersByTimeAsync(6000);
    expect(read).toHaveBeenCalledTimes(1);

    answer.next('manual');
    answer.complete();
    await vi.advanceTimersByTimeAsync(3000);
    expect(read).toHaveBeenCalledTimes(2);
  });

  it('polls nothing while the tab is hidden, and reads at once when it shows again', async () => {
    TestBed.createComponent(Reader);
    await vi.advanceTimersByTimeAsync(0);
    expect(read).toHaveBeenCalledTimes(1);

    visible.next(false);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(read).toHaveBeenCalledTimes(1);

    visible.next(true);
    await vi.advanceTimersByTimeAsync(0);
    expect(read).toHaveBeenCalledTimes(2);
  });

  it('keeps polling after a failed read, saying why until the next answer', async () => {
    read.mockReturnValueOnce(throwError(() => ({ error: { detail: 'docker did not answer' } })));
    TestBed.createComponent(Reader);
    await vi.advanceTimersByTimeAsync(0);
    expect(feed.error()).toBe('docker did not answer');

    await vi.advanceTimersByTimeAsync(3000);
    expect(feed.error()).toBeNull();
    expect(feed.value()).toBe('answer');
  });

  it('lets a slow read finish instead of cancelling it on the next tick', async () => {
    // docker compose ps can take longer than the 3 s tick; a cancelled request kills the ps on the host, so if each
    // tick replaced the last one no answer would ever arrive.
    read.mockImplementation(() => of('slow').pipe(delay(5000)));
    TestBed.createComponent(Reader);

    await vi.advanceTimersByTimeAsync(5000);

    expect(read).toHaveBeenCalledTimes(1);
    expect(feed.value()).toBe('slow');
  });
});

describe('TabVisibility', () => {
  afterEach(() => {
    Reflect.deleteProperty(document, 'visibilityState');
  });

  function showing(state: DocumentVisibilityState) {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
    document.dispatchEvent(new Event('visibilitychange'));
  }

  it('follows the document, and reads anything but hidden as showing', () => {
    const seen: boolean[] = [];
    showing('visible');
    const subscription = TestBed.inject(TabVisibility).visible$.subscribe((v) => seen.push(v));

    showing('hidden');
    showing('visible');
    subscription.unsubscribe();

    expect(seen).toEqual([true, false, true]);
  });
});
