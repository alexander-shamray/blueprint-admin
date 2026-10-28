import { Component, computed, inject, input } from '@angular/core';
import { shows, stringList } from '../../../workspace/widget-config';
import { StackFeed } from '../stack-feed';

/** Whether each platform surface answers (spec §5.3), read with the stack. */
@Component({
  selector: 'app-reachability-widget',
  template: `
    @if (feed.stack.value()) {
      <p class="reachability">
        @for (r of surfaces(); track r.name) {
          <span [class.up]="r.up" [class.down]="!r.up && r.published" [title]="r.url"
            >{{ r.name }}&ngsp;
            @if (r.up) {
              <ng-container i18n>up</ng-container>
            } @else if (!r.published) {
              <ng-container i18n>no host port on the stress override</ng-container>
            } @else {
              <ng-container i18n>down</ng-container>
            }
            @if (r.status !== null) {
              {{ r.status }}
            }
          </span>
        }
      </p>
    } @else if (feed.stack.error(); as e) {
      <p class="error" i18n>The host did not answer: {{ e }}</p>
    } @else {
      <p class="loading" i18n>Loading…</p>
    }
  `,
  styleUrl: './stack-widgets.css',
  preserveWhitespaces: true,
})
export class ReachabilityWidget {
  readonly feed = inject(StackFeed);
  readonly config = input<Record<string, unknown>>({});

  /** The surfaces `config.surfaces` names, or every one when it names none. */
  readonly surfaces = computed(() => {
    const shown = stringList(this.config()['surfaces']);
    return (this.feed.stack.value()?.reachability ?? []).filter((r) => shows(shown, r.name));
  });

  constructor() {
    this.feed.stack.attach();
  }
}
