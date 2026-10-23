import { PlatformMapView } from '../../core/host/host-types';

/** What the Map specs read: the host's answer as the fake platform gives it, in miniature. */
export const view: PlatformMapView = {
  brokerReachable: true,
  error: null,
  services: [
    { name: 'gateway', url: 'http://localhost:5000', routes: [], queues: [] },
    {
      name: 'ordering',
      url: 'http://localhost:5101',
      routes: [{ method: null, prefix: '/api/v1/orders/', policy: 'authenticated' }],
      queues: [
        {
          name: 'ordering-catalog-events',
          consumer: "Ordering's price projection",
          messages: 2,
          parked: null,
          bindings: [{ source: 'Common.Contracts.Catalog.V1:PriceChanged', routingKey: '' }],
        },
        { name: 'ordering-catalog-events_error', consumer: "Ordering's price projection", messages: 1, parked: 'error', bindings: [] },
      ],
    },
    { name: 'shipping', url: null, routes: [], queues: [] },
  ],
  disagreements: ["mystery-queue is on the broker but in no service's queue table."],
};
