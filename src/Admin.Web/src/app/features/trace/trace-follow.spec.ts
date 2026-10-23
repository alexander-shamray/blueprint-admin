import { Component, DestroyRef, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { TraceFollow, TraceLink } from './trace-follow';

@Component({
  selector: 'app-links',
  imports: [TraceLink],
  template: `<a class="link" [appTraceLink]="id">trace</a>`,
})
class Links {
  id = 'corr 1';
}

@Component({ selector: 'app-follower', template: '' })
class Follower {
  constructor() {
    inject(TraceFollow).follow(inject(DestroyRef));
  }
}

describe('TraceFollow', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  it('counts the Trace widgets that follow, and not the ones that were destroyed or released', () => {
    const follow = TestBed.inject(TraceFollow);
    const a = TestBed.createComponent(Follower);
    const b = TestBed.createComponent(Follower);
    expect(follow.followers()).toBe(2);

    a.destroy();
    expect(follow.followers()).toBe(1);
    b.destroy();
    expect(follow.followers()).toBe(0);
  });

  it('releases a follower once, however often it is released', () => {
    const follow = TestBed.inject(TraceFollow);
    const release = follow.follow(TestBed.inject(DestroyRef));

    release();
    release();

    expect(follow.followers()).toBe(0);
  });

  it('numbers a point, so the same id pointed at twice is two points', () => {
    const follow = TestBed.inject(TraceFollow);

    follow.point('a');
    follow.point('a');

    expect(follow.pointed()).toEqual({ id: 'a', n: 2 });
  });

  it('re-points the followers when there are some, and opens the Trace page when there are none', () => {
    const follow = TestBed.inject(TraceFollow);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    follow.open('none-follow');
    expect(navigate).toHaveBeenCalledWith(['/trace', 'none-follow']);
    expect(follow.pointed()).toBeNull();

    TestBed.createComponent(Follower);
    follow.open('someone-follows');
    expect(follow.pointed()).toEqual({ id: 'someone-follows', n: 1 });
    expect(navigate).toHaveBeenCalledTimes(1);
  });
});

describe('TraceLink', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  function render() {
    const fixture = TestBed.createComponent(Links);
    fixture.detectChanges();
    return fixture;
  }

  it('is an ordinary link to the built-in Trace page, so it opens in a new tab as one does', () => {
    const link = render().nativeElement.querySelector('a.link') as HTMLAnchorElement;

    expect(link.getAttribute('href')).toBe('/trace/corr%201');
  });

  it('goes through TraceFollow on a plain click, and leaves the browser the modified ones', () => {
    const follow = TestBed.inject(TraceFollow);
    const open = vi.spyOn(follow, 'open').mockImplementation(() => undefined);
    const link = render().nativeElement.querySelector('a.link') as HTMLAnchorElement;

    const plain = new MouseEvent('click', { button: 0, bubbles: true, cancelable: true });
    link.dispatchEvent(plain);
    expect(plain.defaultPrevented).toBe(true);
    expect(open).toHaveBeenCalledWith('corr 1');

    open.mockClear();
    for (const init of [{ ctrlKey: true }, { metaKey: true }, { shiftKey: true }, { altKey: true }, { button: 1 }]) {
      const event = new MouseEvent('click', { bubbles: true, cancelable: true, ...init });
      link.dispatchEvent(event);
      expect(event.defaultPrevented).toBe(false);
    }
    expect(open).not.toHaveBeenCalled();
  });
});
