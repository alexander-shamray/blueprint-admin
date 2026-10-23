import { InjectionToken, inject } from '@angular/core';

/** Which widget instance a component renders, and on which page; the grid provides it to each widget it creates. */
export interface WidgetContext {
  instanceId: string;
  pageId: string;
}

export const WIDGET_CONTEXT = new InjectionToken<WidgetContext>('WIDGET_CONTEXT');

/** The calling widget's context, or null for a component rendered outside the grid. */
export function injectWidgetContext(): WidgetContext | null {
  return inject(WIDGET_CONTEXT, { optional: true });
}
