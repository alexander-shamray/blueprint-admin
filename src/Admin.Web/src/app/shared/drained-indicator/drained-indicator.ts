import { DecimalPipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { ProjectionDrain } from '../../core/host/host-types';

/**
 * run-locally.md: after publishing a product, wait until ordering-catalog-events drains before placing an
 * order for it, because Ordering prices from its projection, not from Catalog's HTTP API. The state is
 * given in words as well as colour. Drained says only that the queue is empty: the outbox publishes after
 * the request, so the queue can be empty before the event arrives (spec §12, Phase 6). The queue's name is a
 * platform identifier and stays as written in every language (AI assistant design §5.5).
 */
@Component({
  selector: 'app-drained-indicator',
  imports: [DecimalPipe],
  template: `
    @let p = projection();
    <p class="drained" role="status" [class.ok]="p.drained" [class.waiting]="!p.drained">
      @if (p.drained) {
        <ng-container i18n>[drained] {{ p.queue }} is empty: what run-locally.md waits for before placing an order.</ng-container>
      } @else if (!p.found) {
        <ng-container i18n>[not declared] {{ p.queue }} does not exist: Ordering has not started, so it cannot price an order.</ng-container>
      } @else if (p.messages === null) {
        <ng-container i18n>[waiting] {{ p.queue }} holds some messages: Ordering prices orders from its projection, not from Catalog, so an order for a product just published may not find its price yet. Wait for the queue to drain.</ng-container>
      } @else {
        <ng-container i18n>[waiting] {{ p.queue }} holds {p.messages, plural, =1 {1 message} other {{{ p.messages | number }} messages}}: Ordering prices orders from its projection, not from Catalog, so an order for a product just published may not find its price yet. Wait for the queue to drain.</ng-container>
      }
    </p>
  `,
  styles: `
    .drained { padding: 0.25rem 0.5rem; border-radius: 3px; }
    .ok { background: #dfd; }
    .waiting { background: #fec; }
  `,
})
export class DrainedIndicator {
  readonly projection = input.required<ProjectionDrain>();
}
