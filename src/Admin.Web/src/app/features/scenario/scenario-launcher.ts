import { Injectable, signal } from '@angular/core';

/**
 * How long a palette request waits for a Scenario widget to be created. The palette navigates to the Scenario page as
 * it asks, and the widget there loads lazily, so the wait covers that load; a page without a Scenario widget leaves
 * the request to lapse here rather than run unasked when a Scenario widget is next created anywhere. A widget that
 * exists holds the request (`hold`), however long its catalog takes, and drops it when it goes.
 */
export const LAUNCH_WINDOW_MS = 10_000;

/**
 * A run asked for from outside the Scenario screen, by the command palette. It is held here rather than in the URL,
 * so a link, a bookmark or a reload never publishes and orders: only a deliberate command does.
 */
@Injectable({ providedIn: 'root' })
export class ScenarioLauncher {
  private readonly requested = signal(false);
  private lapse: ReturnType<typeof setTimeout> | null = null;

  readonly pending = this.requested.asReadonly();

  request(): void {
    this.requested.set(true);
    if (this.lapse !== null) clearTimeout(this.lapse);
    this.lapse = setTimeout(() => this.take(), LAUNCH_WINDOW_MS);
  }

  /** A Scenario widget is here to take the request: it no longer lapses, and the widget takes or drops it. */
  hold(): void {
    this.stopLapse();
  }

  /** Clears the request and says whether there was one, so exactly one screen acts on it. */
  take(): boolean {
    const was = this.requested();
    this.requested.set(false);
    this.stopLapse();
    return was;
  }

  private stopLapse(): void {
    if (this.lapse !== null) {
      clearTimeout(this.lapse);
      this.lapse = null;
    }
  }
}
