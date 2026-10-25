import { Component, computed, inject, input } from '@angular/core';
import { pollWhenOn } from '../../../workspace/polled-feed';
import { BrokerFeed, exchangeName, filterOf } from '../broker-feed';

/** Exchanges (widget-workspace spec §5.2). Config: a name filter, and the widget's own opt-in auto-refresh. */
@Component({
  selector: 'app-exchanges-widget',
  template: `
    <section class="actions">
      <button class="refresh" [disabled]="feed.exchanges.refreshing()" (click)="feed.exchanges.refresh()" i18n
        >Refresh</button
      >
      @if (autoRefresh()) {
        <span class="auto" i18n>Refreshes every 5 s</span>
      }
    </section>
    @if (feed.exchanges.error(); as e) {
      <p class="error" role="alert" i18n>The host did not answer: {{ e }}</p>
    }
    @if (feed.exchanges.value(); as x) {
      @if (x.reachable) {
        <table class="exchanges">
          <thead
            ><tr><th i18n>Exchange</th><th i18n>Type</th></tr></thead
          >
          <tbody>
            @for (exchange of shown(); track exchange.name) {
              <tr>
                <td>{{ name(exchange.name) }}</td>
                <td>{{ exchange.type }}</td>
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <p class="error" role="alert" i18n>The broker did not answer: {{ x.error }}</p>
      }
    } @else if (!feed.exchanges.error()) {
      <p class="loading" i18n>Loading…</p>
    }
  `,
  styleUrl: './broker-widgets.css',
  preserveWhitespaces: true,
})
export class ExchangesWidget {
  readonly feed = inject(BrokerFeed);
  readonly config = input<Record<string, unknown>>({});
  readonly name = exchangeName;

  readonly autoRefresh = computed(() => this.config()['autoRefresh'] === true);

  readonly shown = computed(() => {
    const needle = filterOf(this.config());
    return (this.feed.exchanges.value()?.exchanges ?? []).filter(
      (e) => !needle || exchangeName(e.name).toLowerCase().includes(needle) || e.type.toLowerCase().includes(needle),
    );
  });

  constructor() {
    pollWhenOn(this.feed.exchanges, this.autoRefresh);
  }
}
