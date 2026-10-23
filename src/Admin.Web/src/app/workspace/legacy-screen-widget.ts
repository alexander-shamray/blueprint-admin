import { NgComponentOutlet } from '@angular/common';
import { Component, Type, effect, input, signal } from '@angular/core';
import { SCREEN_COMPONENTS, isScreenPath } from './screen-components';

/**
 * One of today's screen components, rendered whole (widget-workspace spec §10, phase 1). It is created inside the
 * routed workspace page, so a screen that reads ActivatedRoute (Trace's correlation id, Mail's `?correlationId=`)
 * reads the old route that opened the page.
 */
@Component({
  selector: 'app-legacy-screen-widget',
  imports: [NgComponentOutlet],
  template: `
    @if (component(); as c) {
      <ng-container *ngComponentOutlet="c" />
    } @else {
      <p class="loading">Loading…</p>
    }
  `,
  styles: `
    .loading {
      color: var(--app-muted);
    }
  `,
})
export class LegacyScreenWidget {
  readonly config = input.required<Record<string, unknown>>();
  readonly component = signal<Type<unknown> | null>(null);

  private wanted: unknown = null;

  constructor() {
    effect(() => {
      const screen = this.config()['screen'];
      if (!isScreenPath(screen) || screen === this.wanted) return;
      this.wanted = screen;
      this.component.set(null);
      // A slower import for a screen chosen before this one must not land on top of it.
      void SCREEN_COMPONENTS[screen]().then((c) => {
        if (this.wanted === screen) this.component.set(c);
      });
    });
  }
}
