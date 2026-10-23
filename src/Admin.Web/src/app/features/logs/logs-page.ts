import { Component } from '@angular/core';
import { LogsWidget } from './widgets/logs-widget';

/**
 * The platform's Compose services, owned by blueprint-backend: every service declared in the files
 * deploy/compose/docker-compose.yml includes. The drift gate reads those files against this list
 * (tests/Admin.Host.Tests/Drift/ComposeDriftTests.cs).
 */
export const COMPOSE_SERVICES = [
  'sql', 'redis-cache', 'redis-coordination', 'rabbitmq', 'keycloak', 'otel-collector', 'grafana',
  'catalog-migrator', 'catalog-api', 'gateway', 'ordering-migrator', 'ordering-api',
  'bff-migrator', 'web-bff',
  'inventory-migrator', 'inventory-api', 'payments-migrator', 'psp-simulator', 'payments-api',
  'shipping-migrator', 'carrier-simulator', 'shipping-worker',
  'notifications-migrator', 'mailpit', 'notifications-worker', 'notifications-api', 'push-simulator',
  'privacy-migrator', 'privacy-api', 'object-store', 'object-store-init', 'image-edge',
];

/**
 * The Logs screen as the legacy screen widget renders it (spec §10): the Logs widget over every service. The page's
 * own follow is gone; the one follow is `LogFollowService`'s, and survives leaving the screen. Retired with the
 * legacy screens (phase 6).
 */
@Component({
  selector: 'app-logs-page',
  imports: [LogsWidget],
  templateUrl: './logs-page.html',
  styleUrl: './logs-page.css',
})
export class LogsPage {}
