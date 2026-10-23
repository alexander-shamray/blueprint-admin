import { Component } from '@angular/core';
import { RunbooksWidget } from './widgets/runbooks-widget';

/**
 * The Runbooks screen as the legacy screen widget renders it (spec §10): the Runbooks widget. Retired with the legacy
 * screens (phase 6).
 */
@Component({
  selector: 'app-runbooks-page',
  imports: [RunbooksWidget],
  templateUrl: './runbooks-page.html',
  styleUrl: './runbooks-page.css',
})
export class RunbooksPage {}
