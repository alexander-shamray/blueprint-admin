import { Injectable, computed, signal } from '@angular/core';

/** One piece of running work as the shell says it: what is going, and the page that shows it. */
export interface RunningItem {
  text: string;
  link: string;
  /** What the item does instead of following its link, for work a panel rather than a page shows. */
  open?: () => void;
}

/**
 * The running work the shell's indicator lists (widget-workspace spec §4.3, §7.2): a Scenario run, a Compose job,
 * whatever goes on while no widget shows it. Each source reports itself here once, when it is first created, so the
 * shell names none of them and loads none of their code; a source nobody has used yet has nothing running.
 */
@Injectable({ providedIn: 'root' })
export class RunningWork {
  private readonly sources = signal<ReadonlyMap<string, () => RunningItem | null>>(new Map());

  /** What is running now, in the order the sources first reported. */
  readonly items = computed(() =>
    [...this.sources().values()].map((read) => read()).filter((item): item is RunningItem => item !== null),
  );

  /** Adds a source under `key`; `read` is evaluated reactively, so it reads signals and answers null while idle. */
  report(key: string, read: () => RunningItem | null): void {
    this.sources.update((sources) => new Map(sources).set(key, read));
  }
}
