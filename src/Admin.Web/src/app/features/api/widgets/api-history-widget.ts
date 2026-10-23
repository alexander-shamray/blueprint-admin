import { Component, inject } from '@angular/core';
import { ApiHistory } from '../api-client';

/**
 * The calls this tab has sent from an API request widget (widget-workspace spec §5.2), newest first. Restoring one puts
 * it back in an API request widget on the page, so it is offered only while there is one; the entries themselves are
 * `ApiHistory`'s and carry no credential.
 */
@Component({
  selector: 'app-api-history-widget',
  template: `
    @if (history.entries().length > 0) {
      <section class="history">
        @if (!history.canRestore()) {
          <p class="note">Add an API request widget to this page to put a call back.</p>
        }
        <ul>
          @for (h of history.entries(); track h.seq) {
            <li>
              @if (history.canRestore()) {
                <button (click)="history.requestRestore(h)">{{ history.label(h) }}</button>
              } @else {
                <span class="entry">{{ history.label(h) }}</span>
              }
            </li>
          }
        </ul>
      </section>
    } @else {
      <p class="empty">Nothing has been sent from this tab yet.</p>
    }
  `,
  styles: `
    .history ul {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .history button {
      background: none;
      border: 0;
      cursor: pointer;
      font-family: monospace;
      padding: 0.1rem 0;
      color: inherit;
      text-align: left;
    }
    .entry {
      font-family: monospace;
    }
    .empty,
    .note {
      color: var(--app-muted);
    }
  `,
})
export class ApiHistoryWidget {
  readonly history = inject(ApiHistory);
}
