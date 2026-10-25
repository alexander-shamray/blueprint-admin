import { Component, computed, inject, input } from '@angular/core';
import { shows, stringList } from '../../../workspace/widget-config';
import { StackFeed } from '../stack-feed';

/** The golden-signal strip (spec §5.8): each dashboard panel per service, on its own 15-second poll. */
@Component({
  selector: 'app-golden-signals-widget',
  template: `
    <section class="signals" i18n-aria-label aria-label="Golden signals">
      @if (feed.health.error(); as e) {
        <p class="error" i18n>The host did not answer for the golden signals: {{ e }}</p>
      }
      @if (feed.health.value(); as h) {
        @if (!h.reachable) {
          <p class="error" i18n>Golden signals unavailable: {{ h.error }}</p>
        } @else if (h.services.length === 0) {
          <p class="signals-empty" i18n>No requests recorded yet.</p>
        } @else {
          <table class="signals">
            <thead>
              <tr>
                <th i18n>Service</th><th i18n>Requests (req/s)</th><th i18n>5xx share</th><th i18n>Latency p99</th>
                <th i18n>422 (req/s)</th><th i18n>401 (req/s)</th><th i18n>Command p95</th><th i18n>Query p95</th>
              </tr>
            </thead>
            <tbody>
              @for (svc of services(); track svc.service) {
                <tr>
                  <td>{{ svc.service }}</td>
                  <td>{{ rate(svc.requestRate) }}</td>
                  <td>{{ ratio(svc.errorRatio) }}</td>
                  <td>{{ duration(svc.latencyP99Seconds) }}</td>
                  <td>{{ rate(svc.domainRefusalRate) }}</td>
                  <td>{{ rate(svc.unauthorisedRate) }}</td>
                  <td>{{ duration(svc.commandP95Seconds) }}</td>
                  <td>{{ duration(svc.queryP95Seconds) }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
      } @else if (!feed.health.error()) {
        <p class="loading" i18n>Loading…</p>
      }
    </section>
  `,
  styleUrl: './stack-widgets.css',
  preserveWhitespaces: true,
})
export class GoldenSignalsWidget {
  readonly feed = inject(StackFeed);
  readonly config = input<Record<string, unknown>>({});

  /** The services `config.services` names, or every one when it names none. */
  readonly services = computed(() => {
    const shown = stringList(this.config()['services']);
    return (this.feed.health.value()?.services ?? []).filter((s) => shows(shown, s.service));
  });

  constructor() {
    this.feed.health.attach();
  }

  /**
   * A rate, a share and a duration as the strip's cells print them; a rate's unit is in its column header, a share
   * and a duration carry theirs in the cell, and absent is a dash, not a zero. A rate keeps two significant figures,
   * because one refusal in a 5-minute window is a few thousandths per second and fixed places would print it as a zero.
   */
  rate(value: number | null): string {
    return value === null ? '—' : String(Number(value.toPrecision(2)));
  }

  ratio(value: number | null): string {
    return value === null ? '—' : `${(value * 100).toFixed(1)} %`;
  }

  duration(seconds: number | null): string {
    return seconds === null ? '—' : `${Math.round(seconds * 1000)} ms`;
  }
}
