import { Injector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { WIDGET_CONTEXT } from './widget-context';
import { WidgetSessionStore, widgetSession } from './widget-session-store';

/** `widgetSession` as a widget's constructor calls it: inside the injector the grid gives the instance, or none. */
function session(instanceId: string | null): ReturnType<typeof widgetSession<string>> {
  const parent = TestBed.inject(Injector);
  const injector = instanceId
    ? Injector.create({ providers: [{ provide: WIDGET_CONTEXT, useValue: { instanceId, pageId: 'p' } }], parent })
    : parent;
  return runInInjectionContext(injector, () => widgetSession('filter', ''));
}

describe('WidgetSessionStore', () => {
  it('keeps what an instance typed across a destroy, and apart from every other instance', () => {
    session('w1').set('orders');

    expect(session('w1')()).toBe('orders');
    expect(session('w2')()).toBe('');
    expect(TestBed.inject(WidgetSessionStore).state('w1', 'other', 0)()).toBe(0);
  });

  it('forgets an instance that left the workspace', () => {
    session('w1').set('orders');

    TestBed.inject(WidgetSessionStore).forget(['w1']);

    expect(session('w1')()).toBe('');
  });

  it('gives a component outside the grid a state of its own that ends with it', () => {
    session(null).set('orders');

    expect(session(null)()).toBe('');
  });
});
