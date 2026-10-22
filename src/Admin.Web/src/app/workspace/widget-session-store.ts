import { Injectable, WritableSignal, inject, signal } from '@angular/core';
import { injectWidgetContext } from './widget-context';

/**
 * Operator input in flight, per widget instance (widget-workspace spec §4.2): a body being written, a filter typed.
 * It outlives the widget's component, so leaving a page and coming back finds it, and ends with the tab. A typed
 * confirmation, or any step that unlocks a destructive action, is never kept here: it lives in the component and is
 * empty on every render.
 */
@Injectable({ providedIn: 'root' })
export class WidgetSessionStore {
  private readonly instances = new Map<string, Map<string, WritableSignal<unknown>>>();

  /** The instance's state under `key`: the signal it held when it was last destroyed, or a new one holding `initial`. */
  state<T>(instanceId: string, key: string, initial: T): WritableSignal<T> {
    let states = this.instances.get(instanceId);
    if (!states) {
      states = new Map();
      this.instances.set(instanceId, states);
    }
    let state = states.get(key);
    if (!state) {
      state = signal<unknown>(initial);
      states.set(key, state);
    }
    return state as WritableSignal<T>;
  }

  /**
   * Drops what instances kept, when a remove or a Reset page takes them off their page. A deleted page keeps its
   * widgets' state while its undo is offered; a later add never reuses an id, so nothing reads that state again.
   */
  forget(instanceIds: Iterable<string>): void {
    for (const id of instanceIds) this.instances.delete(id);
  }
}

/**
 * The calling widget's session state under `key`; call it from the widget's constructor. A component rendered
 * outside the grid has no instance to keep it for, so it gets a plain signal that ends with it.
 */
export function widgetSession<T>(key: string, initial: T): WritableSignal<T> {
  const context = injectWidgetContext();
  return context ? inject(WidgetSessionStore).state(context.instanceId, key, initial) : signal(initial);
}
