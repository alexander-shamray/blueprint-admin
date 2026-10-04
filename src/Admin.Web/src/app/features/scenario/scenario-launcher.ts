import { Injectable, signal } from '@angular/core';

/**
 * A run asked for from outside the Scenario screen, by the command palette. It is held here rather than in the URL,
 * so a link, a bookmark or a reload never publishes and orders: only a deliberate command does.
 */
@Injectable({ providedIn: 'root' })
export class ScenarioLauncher {
  private readonly requested = signal(false);

  readonly pending = this.requested.asReadonly();

  request(): void {
    this.requested.set(true);
  }

  /** Clears the request and says whether there was one, so exactly one screen acts on it. */
  take(): boolean {
    const was = this.requested();
    this.requested.set(false);
    return was;
  }
}
