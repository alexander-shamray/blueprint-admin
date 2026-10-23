import { Component } from '@angular/core';
import { MapWidget } from './widgets/map-widget';

/**
 * The Map screen as the legacy screen widget renders it (spec §10): the Platform map widget. Its reads are `MapFeed`'s.
 * It does not poll: auto-refresh is a setting of a widget on a workspace page, which this screen has no place for.
 * Retired with the legacy screens (phase 6).
 */
@Component({
  selector: 'app-map-page',
  imports: [MapWidget],
  templateUrl: './map-page.html',
  styleUrl: './map-page.css',
})
export class MapPage {}
