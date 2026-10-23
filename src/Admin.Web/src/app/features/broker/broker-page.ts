import { Component } from '@angular/core';
import { BffProjectionWidget } from './widgets/bff-projection-widget';
import { BindingsWidget } from './widgets/bindings-widget';
import { DeadLetterWidget } from './widgets/dead-letter-widget';
import { ExchangesWidget } from './widgets/exchanges-widget';
import { PermissionsWidget } from './widgets/permissions-widget';
import { QueuesWidget } from './widgets/queues-widget';

/**
 * The Broker screen as the legacy screen widget renders it (spec §10): the six Broker widgets one under another,
 * each with the settings its type starts with. The screen's own reads are gone; they are `BrokerFeed`'s. It has no
 * page-wide Refresh and does not poll: each widget has its own Refresh, and auto-refresh is a setting of a widget
 * on a workspace page, which this screen has no place for. Retired with the legacy screens (phase 6).
 */
@Component({
  selector: 'app-broker-page',
  imports: [QueuesWidget, BffProjectionWidget, DeadLetterWidget, ExchangesWidget, BindingsWidget, PermissionsWidget],
  templateUrl: './broker-page.html',
  styleUrl: './broker-page.css',
})
export class BrokerPage {}
