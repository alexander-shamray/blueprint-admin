import { Component } from '@angular/core';
import { TraceWidget } from './widgets/trace-widget';

/**
 * The Trace screen as the legacy screen widget renders it (spec §10): the Trace widget, which reads the route's
 * correlation id itself. Retired with the legacy screens (phase 6).
 */
@Component({
  selector: 'app-trace-page',
  imports: [TraceWidget],
  templateUrl: './trace-page.html',
  styleUrl: './trace-page.css',
})
export class TracePage {}
