import { Component, input } from '@angular/core';
import { ProjectionDrain } from '../../core/host/host-types';

/**
 * run-locally.md: after publishing a product, wait until ordering-catalog-events drains before placing an
 * order for it, because Ordering prices from its projection, not from Catalog's HTTP API. The state is
 * given in words as well as colour.
 */
@Component({
  selector: 'app-drained-indicator',
  template: `
    @let p = projection();
    <p class="drained" role="status" [class.ok]="p.drained" [class.waiting]="!p.drained">
      @if (p.drained) {
        [drained] {{ p.queue }} is empty: Ordering's price projection has caught up.
      } @else if (!p.found) {
        [not declared] {{ p.queue }} does not exist: Ordering has not started, so it cannot price an order.
      } @else {
        [waiting] {{ p.queue }} holds {{ p.messages ?? 'some' }} {{ p.messages === 1 ? 'message' : 'messages' }}: Ordering prices orders from its projection, not from Catalog, so an order for a product just published may not find its price yet. Wait for the queue to drain.
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
