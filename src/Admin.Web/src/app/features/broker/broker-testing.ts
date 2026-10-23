import { of } from 'rxjs';
import { vi } from 'vitest';
import {
  BindingsView,
  DeadLetterInspection,
  DeadLetterPlan,
  ExchangesView,
  JobSummary,
  PermissionsView,
  ProjectionPlan,
  QueuesView,
} from '../../core/host/host-types';

/** What the Broker widgets' specs read: the host's answers as the fake platform gives them, and a mocked HostClient. */

export const queues: QueuesView = {
  reachable: true,
  error: null,
  queues: [
    {
      name: 'ordering-catalog-events',
      messages: 2,
      consumer: "Ordering's price projection",
      parked: null,
      runbook: null,
    },
    {
      name: 'ordering-catalog-events_error',
      messages: 1,
      consumer: "Ordering's price projection",
      parked: 'error',
      runbook: 'https://example.test/runbooks/error-queue.md',
    },
    {
      name: 'bff-order-events_skipped',
      messages: 1,
      consumer: "the BFF's order projection",
      parked: 'skipped',
      runbook: 'https://example.test/runbooks/skipped-queue.md',
    },
    {
      name: 'payments-events_skipped',
      messages: 0,
      consumer: "Payments' order events",
      parked: 'skipped',
      runbook: 'https://example.test/runbooks/skipped-queue.md',
    },
    { name: 'something-else', messages: 0, consumer: null, parked: null, runbook: null },
    { name: 'bff-order-events', messages: 0, consumer: "the BFF's order projection", parked: null, runbook: null },
  ],
  projection: { queue: 'ordering-catalog-events', found: true, messages: 2, drained: false },
};

export const plan: ProjectionPlan = {
  queue: 'bff-order-events',
  rebuildConfirm: 'bff-replay --reset',
  limits: ['A replay reaches back one outbox window and no further.', 'A rebuild deletes the projection first.'],
};

export const deadLetterPlan: DeadLetterPlan = {
  executeConfirm: '--execute',
  inspectLimit: 20,
  recordDir: '/data/dead-letters',
};

export const inspection: DeadLetterInspection = {
  queue: 'ordering-catalog-events_error',
  endpoint: 'ordering-catalog-events',
  kind: 'error',
  error: null,
  messages: [
    {
      messageId: 'm-1',
      correlationId: 'corr-1',
      messageType: 'urn:message:Common.Contracts.Catalog.V1:PriceChanged',
      faultMessage: 'no row in the price projection',
      faultExceptionType: 'System.InvalidOperationException',
      inputAddress: 'rabbitmq://rabbitmq/ordering-catalog-events',
      reason: 'fault',
      redelivered: true,
      body: '{"message":{}}',
    },
    {
      messageId: null,
      correlationId: null,
      messageType: null,
      faultMessage: null,
      faultExceptionType: null,
      inputAddress: null,
      reason: null,
      redelivered: false,
      body: null,
    },
  ],
};

export const job: JobSummary = {
  id: 'job-7',
  commandLine: 'bff-replay',
  state: 'Running',
  exitCode: null,
  startedAt: '2026-10-15T00:00:00Z',
};

export const exchanges: ExchangesView = {
  reachable: true,
  error: null,
  exchanges: [
    { name: '', type: 'direct' },
    { name: 'ordering-fulfilment-saga_delay', type: 'x-delayed-message' },
  ],
};

// run-locally.md's two bindings for Catalog's price changes reaching Ordering, and the default exchange's to the queue.
export const bindings: BindingsView = {
  reachable: true,
  error: null,
  bindings: [
    {
      source: '',
      sourceKind: 'exchange',
      destination: 'ordering-catalog-events',
      destinationKind: 'queue',
      routingKey: 'ordering-catalog-events',
    },
    {
      source: 'Common.Contracts.Catalog.V1:PriceChanged',
      sourceKind: 'exchange',
      destination: 'ordering-catalog-events',
      destinationKind: 'exchange',
      routingKey: '',
    },
    {
      source: 'ordering-catalog-events',
      sourceKind: 'exchange',
      destination: 'ordering-catalog-events',
      destinationKind: 'queue',
      routingKey: '',
    },
  ],
};

export const permissions: PermissionsView = {
  reachable: true,
  error: null,
  permissions: [
    { user: 'catalog-svc', configure: '^(MassTransit:)', write: '^(MassTransit:)', read: '^(MassTransit:)' },
  ],
};

/** A HostClient whose every broker read answers at once; a test swaps one with `mockReturnValue`. */
export function brokerHost() {
  return {
    brokerQueues: vi.fn(() => of(queues)),
    brokerExchanges: vi.fn(() => of(exchanges)),
    brokerBindings: vi.fn(() => of(bindings)),
    brokerPermissions: vi.fn(() => of(permissions)),
    projectionPlan: vi.fn(() => of(plan)),
    projectionRepair: vi.fn(() => of(job)),
    projectionRebuild: vi.fn(() => of(job)),
    deadLetterPlan: vi.fn(() => of(deadLetterPlan)),
    deadLetters: vi.fn(() => of(inspection)),
    deadLetterMove: vi.fn(() => of(job)),
  };
}

/** The text of each body row of `table.<name>`, whitespace collapsed. */
export function rows(fixture: { nativeElement: HTMLElement }, table: string): string[] {
  return (Array.from(fixture.nativeElement.querySelectorAll(`table.${table} tbody tr`)) as HTMLElement[]).map(
    (r) => r.textContent?.replace(/\s+/g, ' ').trim() ?? '',
  );
}
