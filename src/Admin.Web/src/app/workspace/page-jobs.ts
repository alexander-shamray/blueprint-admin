import { Injectable, Signal, computed, inject, signal } from '@angular/core';
import { injectWidgetContext } from './widget-context';

/**
 * The job each page started or opened last, which a Job output widget set to follow its page shows (widget-workspace
 * spec §5.2). A job is running work, so the choice outlives the page's widgets: leaving a page and coming back finds
 * the same job, and ends with the tab.
 */
@Injectable({ providedIn: 'root' })
export class PageJobs {
  private readonly jobs = signal<ReadonlyMap<string, string>>(new Map());

  of(pageId: string): Signal<string | null> {
    return computed(() => this.jobs().get(pageId) ?? null);
  }

  show(pageId: string, jobId: string): void {
    this.jobs.update((jobs) => new Map(jobs).set(pageId, jobId));
  }
}

/** The calling widget's page's job, and how to point it at another; call it from the widget's constructor. */
export function injectPageJob(): { id: Signal<string | null>; show: (jobId: string) => void } {
  const jobs = inject(PageJobs);
  // A component outside the grid has no page; it shares one with every other such component, as a screen did.
  const page = injectWidgetContext()?.pageId ?? '';
  return { id: jobs.of(page), show: (jobId) => jobs.show(page, jobId) };
}
