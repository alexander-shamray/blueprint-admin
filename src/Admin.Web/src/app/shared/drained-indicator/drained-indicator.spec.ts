import { TestBed } from '@angular/core/testing';
import { ProjectionDrain } from '../../core/host/host-types';
import { DrainedIndicator } from './drained-indicator';

function render(projection: ProjectionDrain): string {
  const fixture = TestBed.createComponent(DrainedIndicator);
  fixture.componentRef.setInput('projection', projection);
  fixture.detectChanges();
  return (fixture.nativeElement.textContent as string).replace(/\s+/g, ' ').trim();
}

describe('DrainedIndicator', () => {
  it('says the projection has caught up when its queue is empty', () => {
    expect(render({ queue: 'ordering-catalog-events', found: true, messages: 0, drained: true }))
      .toBe("[drained] ordering-catalog-events is empty: Ordering's price projection has caught up.");
  });

  it('is a polite live region so a screen reader hears the projection change', () => {
    const fixture = TestBed.createComponent(DrainedIndicator);
    fixture.componentRef.setInput('projection', { queue: 'ordering-catalog-events', found: true, messages: 3, drained: false });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.drained')?.getAttribute('role')).toBe('status');
  });

  it('says why an order should wait while messages are queued', () => {
    expect(render({ queue: 'ordering-catalog-events', found: true, messages: 3, drained: false }))
      .toBe('[waiting] ordering-catalog-events holds 3 messages: Ordering prices orders from its projection, not from Catalog, so an order for a product just published may not find its price yet. Wait for the queue to drain.');
  });

  it('says the queue is not declared when Ordering has not started', () => {
    expect(render({ queue: 'ordering-catalog-events', found: false, messages: null, drained: false }))
      .toBe('[not declared] ordering-catalog-events does not exist: Ordering has not started, so it cannot price an order.');
  });
});
