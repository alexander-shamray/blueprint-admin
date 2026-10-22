import { Injectable, signal } from '@angular/core';

/**
 * How long a palette request waits for a Scenario widget to take it. The palette navigates to the Scenario page as it
 * asks, and the widget there loads lazily, so the wait covers that load; a page without a Scenario widget leaves the
 * request to lapse here rather than run unasked when a Scenario widget is next created anywhere.
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

  /** Clears the request and says whether there was one, so exactly one screen acts on it. */
  take(): boolean {
    const was = this.requested();
    this.requested.set(false);
    if (this.lapse !== null) {
      clearTimeout(this.lapse);
      this.lapse = null;
    }
    return was;
  }
}
