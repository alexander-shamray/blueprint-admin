import { Component, DestroyRef, computed, inject, input } from '@angular/core';
import { pollWhenOn } from '../../../workspace/polled-feed';
import { MapFeed } from '../map-feed';

/**
 * The platform as the running stack shows it (spec §5.15), as a table: per service the routes that reach it and the
 * queues it consumes, each with its depth and the depth's change per second between two reads. Nothing here is drawn
 * by hand; the host joins its own tables with the broker's answer, and a place where they differ is listed, not
 * resolved. Config: `autoRefresh`, off until switched on (§4.3).
 */
@Component({
  selector: 'app-map-widget',
  templateUrl: './map-widget.html',
  styleUrl: './map-widget.css',
  preserveWhitespaces: true,
})
export class MapWidget {
  readonly feed = inject(MapFeed);
  readonly config = input<Record<string, unknown>>({});

  /** A service's section name. */
  serviceLabel = (name: string): string => $localize`Service ${name}:name:`;

  readonly autoRefresh = computed(() => this.config()['autoRefresh'] === true);

  constructor() {
    this.feed.show(inject(DestroyRef));
    pollWhenOn(this.feed.map, this.autoRefresh);
  }
}
