import { Component } from '@angular/core';
import { ApiHistoryWidget } from './widgets/api-history-widget';
import { ApiRequestWidget } from './widgets/api-request-widget';

// What the Scenario screen and its runner import from here; the definitions are api-client.ts's.
export { DRAIN_POLL_MS, DRAIN_WATCH_MS, PUBLISH_OPERATION, UUID } from './api-client';
export type { HistoryEntry, SentAs } from './api-client';
export { clockText } from './api-client';

/**
 * The API screen as the legacy screen widget renders it (spec §10): the API request widget with the API history widget
 * under it. Their state is `ApiHistory`'s and the identity's. Retired with the legacy screens (phase 6).
 */
@Component({
  selector: 'app-api-page',
  imports: [ApiRequestWidget, ApiHistoryWidget],
  templateUrl: './api-page.html',
  styleUrl: './api-page.css',
})
export class ApiPage {}
