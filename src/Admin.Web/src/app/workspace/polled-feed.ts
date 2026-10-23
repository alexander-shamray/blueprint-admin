import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  BehaviorSubject,
  EMPTY,
  Observable,
  catchError,
  combineLatest,
  defer,
  distinctUntilChanged,
  exhaustMap,
  finalize,
  fromEvent,
  map,
  startWith,
  switchMap,
  tap,
  timer,
} from 'rxjs';
import { hostErrorText } from '../core/host/host-error';

/** Whether the browser tab is showing, which a poll follows (widget-workspace spec §4.3). */
@Injectable({ providedIn: 'root' })
export class TabVisibility {
  private readonly document = inject(DOCUMENT);

  /** Anything but `hidden` reads as showing: a document still being prerendered has nobody to hide it from yet. */
  readonly visible$: Observable<boolean> = fromEvent(this.document, 'visibilitychange').pipe(
    startWith(null),
    map(() => this.document.visibilityState !== 'hidden'),
    distinctUntilChanged(),
  );
}

/**
 * One source a data service polls (widget-workspace spec §4.3): it reads only while at least one widget is attached
 * and the tab is visible, and every attached widget shares the one poll. Widgets on another page are destroyed, so
 * a page that is not showing holds no subscriber and polls nothing.
 *
 * `exhaustMap` ignores ticks while a read is in flight rather than cancelling it: a `docker compose ps` can take
 * longer than a tick (the host allows it 30 s), and a cancelled request makes the host kill that ps, so replacing it
 * each tick would starve the poll with no answer and no error. A failed read is caught inside, so it does not end
 * the timer; the last good value stays while `error` says why the newest is missing.
 */
export class PolledFeed<T> {
  readonly value = signal<T | undefined>(undefined);
  /** Set when a read fails at the transport, cleared on the next answer. */
  readonly error = signal<string | null>(null);
  /** A read is out, whether the timer's or a `refresh()`'s. */
  readonly refreshing = signal(false);

  private readonly subscribers = new BehaviorSubject(0);

  constructor(
    private readonly read: () => Observable<T>,
    periodMs: number,
    visible: Observable<boolean>,
    private readonly destroyRef: DestroyRef,
  ) {
    combineLatest([this.subscribers.pipe(map((n) => n > 0)), visible])
      .pipe(
        map(([wanted, showing]) => wanted && showing),
        distinctUntilChanged(),
        switchMap((on) =>
          // A tick is skipped while a manual read is out, as it is while its own is: one read at a time.
          on ? timer(0, periodMs).pipe(exhaustMap(() => (this.refreshing() ? EMPTY : this.fetch()))) : EMPTY,
        ),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe();
  }

  /**
   * One read now, for a widget whose own auto-refresh is off or that has just done something the feed should show.
   * Nothing is started while a read is out: it may have been asked before what the caller is asking about, so
   * callers that must read afterwards watch `refreshing` and ask again.
   */
  refresh(): void {
    if (this.refreshing()) return;
    this.fetch().pipe(takeUntilDestroyed(this.destroyRef)).subscribe();
  }

  /** A read, with its answer and its failure recorded; a failure ends this read and not the poll. */
  private fetch(): Observable<T> {
    return defer(() => {
      this.refreshing.set(true);
      return this.read().pipe(
        tap((value) => {
          this.error.set(null);
          this.value.set(value);
        }),
        catchError((e: unknown) => {
          this.error.set(hostErrorText(e));
          return EMPTY;
        }),
        finalize(() => this.refreshing.set(false)),
      );
    });
  }

  /**
   * Subscribes the calling widget until it is destroyed; call it from the widget's constructor. The function it
   * returns ends the subscription sooner, for a widget that polls only while a setting is on; ending it twice, or
   * again at destruction, counts once.
   */
  attach(destroyRef = inject(DestroyRef)): () => void {
    this.subscribers.next(this.subscribers.value + 1);
    let released = false;
    const release = (): void => {
      if (released) return;
      released = true;
      this.subscribers.next(this.subscribers.value - 1);
    };
    destroyRef.onDestroy(release);
    return release;
  }
}
