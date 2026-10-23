import { Component } from '@angular/core';
import { LogsWidget } from './widgets/logs-widget';

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
