import { Component, DestroyRef, inject, input } from '@angular/core';
import { AssistantPanel, STATUS_REASON_TEXT } from './assistant-panel';
import { AssistantService } from './assistant-service';

/**
 * The Assistant widget (AI assistant design §6.4, phase 4): a view of the tab's one conversation, the same as the side
 * panel, so a page can hold it beside what it asks about. With the assistant off a saved instance renders a notice
 * instead of the conversation, and nothing of the assistant runs (§9.1).
 */
@Component({
  selector: 'app-assistant-widget',
  imports: [AssistantPanel],
  template: `
    @if (assistant.enabled()) {
      <app-assistant-panel [embedded]="true" />
    } @else if (assistant.status() !== null) {
      <p class="off" role="status">{{ off }}</p>
    }
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }
    .off {
      margin: 0;
      padding: 8px 12px;
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class AssistantWidget {
  /** Every widget is given its saved config (§6.3); this one has none to read. */
  readonly config = input<Record<string, unknown>>({});
  readonly assistant = inject(AssistantService);
  readonly off = STATUS_REASON_TEXT.off;

  constructor() {
    // Showing counts as the panel being open for the conversation's event stream, which an idle, hidden one does not hold.
    this.assistant.show(inject(DestroyRef));
  }
}
