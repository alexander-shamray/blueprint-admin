/**
 * The platform's Compose services, owned by blueprint-backend: every service declared in the files
 * deploy/compose/docker-compose.yml includes. The drift gate reads those files against this list
 * (tests/Admin.Host.Tests/Drift/ComposeDriftTests.cs). The Logs widget checks the services it is configured for
 * against it, so a misspelt name is said rather than shown as a widget that never gets a line.
 */
export const COMPOSE_SERVICES = [
  'sql',
  'redis-cache',
  'redis-coordination',
  'rabbitmq',
  'keycloak',
  'otel-collector',
  'grafana',
  'catalog-migrator',
  'catalog-api',
  'gateway',
  'ordering-migrator',
  'ordering-api',
  'bff-migrator',
  'web-bff',
  'inventory-migrator',
  'inventory-api',
  'payments-migrator',
  'psp-simulator',
  'payments-api',
  'shipping-migrator',
  'carrier-simulator',
  'shipping-worker',
  'notifications-migrator',
  'mailpit',
  'notifications-worker',
  'notifications-api',
  'push-simulator',
  'privacy-migrator',
  'privacy-api',
  'object-store',
  'object-store-init',
  'image-edge',
];
