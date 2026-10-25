import { Component, computed, inject, input } from '@angular/core';
import { pollWhenOn } from '../../../workspace/polled-feed';
import { BrokerFeed, exchangeName, filterOf } from '../broker-feed';

/** Bindings (widget-workspace spec §5.2). Config: a filter on any column, and the widget's own opt-in auto-refresh. */
@Component({
  selector: 'app-bindings-widget',
  template: `
    <section class="actions">
      <button class="refresh" [disabled]="feed.bindings.refreshing()" (click)="feed.bindings.refresh()" i18n
        >Refresh</button
      >
      @if (autoRefresh()) {
        <span class="auto" i18n>Refreshes every 5 s</span>
      }
    </section>
    @if (feed.bindings.error(); as e) {
      <p class="error" role="alert" i18n>The host did not answer: {{ e }}</p>
    }
    @if (feed.bindings.value(); as b) {
      @if (b.reachable) {
        <table class="bindings">
          <thead
            ><tr><th i18n>Source</th><th i18n>Destination</th><th i18n>Kind</th><th i18n>Routing key</th></tr></thead
          >
          <tbody>
            @for (binding of shown(); track $index) {
              <tr>
                <td>{{ name(binding.source) }}</td>
                <td>{{ binding.destination }}</td>
                <td>{{ binding.destinationKind }}</td>
                <td
                  ><code>{{ binding.routingKey }}</code></td
                >
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <p class="error" role="alert" i18n>The broker did not answer: {{ b.error }}</p>
      }
    } @else if (!feed.bindings.error()) {
      <p class="loading" i18n>Loading…</p>
    }
  `,
  styleUrl: './broker-widgets.css',
  preserveWhitespaces: true,
})
export class BindingsWidget {
  readonly feed = inject(BrokerFeed);
  readonly config = input<Record<string, unknown>>({});
  readonly name = exchangeName;

  readonly autoRefresh = computed(() => this.config()['autoRefresh'] === true);

  readonly shown = computed(() => {
    const needle = filterOf(this.config());
    return (this.feed.bindings.value()?.bindings ?? []).filter(
      (b) =>
        !needle ||
        [exchangeName(b.source), b.destination, b.destinationKind, b.routingKey].some((cell) =>
          cell.toLowerCase().includes(needle),
        ),
    );
  });

  constructor() {
    pollWhenOn(this.feed.bindings, this.autoRefresh);
  }
}
