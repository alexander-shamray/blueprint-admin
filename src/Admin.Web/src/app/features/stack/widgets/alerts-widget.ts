import { Component, computed, inject, input } from '@angular/core';
import { StackFeed } from '../stack-feed';

/**
 * The alert strip (spec §5.8): each loaded rule with its state, and the rules written out but not loaded apart, on the
 * signals' 15-second poll. `config.firingOnly` leaves the loaded rules that are not firing out.
 */
@Component({
  selector: 'app-alerts-widget',
  template: `
    <section class="alerts" i18n-aria-label aria-label="Alerts">
      @if (feed.alerts.error(); as e) {
        <p class="error" i18n>The host did not answer for the alert rules: {{ e }}</p>
      }
      @if (feed.alerts.value(); as a) {
        @if (!a.reachable) {
          <p class="error" i18n>Alert rules unavailable: {{ a.error }}</p>
        } @else if (a.loaded.length === 0) {
          <p class="alerts-empty" i18n
            >Prometheus has loaded no alert rules, so an empty strip here is not a quiet platform.</p
          >
        } @else if (rules().length === 0) {
          <p class="alerts-empty" i18n>No rule is firing.</p>
        } @else {
          <table class="alerts">
            <thead
              ><tr><th i18n>Rule</th><th i18n>State</th><th i18n>Severity</th><th i18n>Alight</th></tr></thead
            >
            <tbody>
              @for (rule of rules(); track rule.group + '/' + rule.name) {
                <tr [class]="'state-' + rule.state" [title]="rule.summary ?? ''">
                  <td>{{ rule.name }}</td>
                  <td>
                    @switch (rule.state) {
                      @case ('firing') {
                        <ng-container i18n>firing</ng-container>
                      }
                      @case ('pending') {
                        <ng-container i18n>pending</ng-container>
                      }
                      @case ('inactive') {
                        <ng-container i18n>inactive</ng-container>
                      }
                      @default {
                        {{ rule.state }}
                      }
                    }
                    @if (rule.health === 'err') {
                      <ng-container i18n>(evaluation failing)</ng-container>
                    }
                  </td>
                  <td>{{ rule.severity ?? '—' }}</td>
                  <td>{{ rule.alerts }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
        @if (a.waiting.length > 0) {
          <p class="alerts-waiting" i18n>
            Waiting, not quiet: these rules are written out and not loaded, because the instrument each reads is not
            published yet. A rule that cannot fire says nothing about health.
          </p>
          <table class="waiting">
            <thead
              ><tr><th i18n>Rule</th><th i18n>Waiting on</th></tr></thead
            >
            <tbody>
              @for (rule of a.waiting; track rule.name) {
                <tr>
                  <td>{{ rule.name }}</td>
                  <td>{{ rule.instruments.join(', ') }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
      } @else if (!feed.alerts.error()) {
        <p class="loading" i18n>Loading…</p>
      }
    </section>
  `,
  styleUrl: './stack-widgets.css',
  preserveWhitespaces: true,
})
export class AlertsWidget {
  readonly feed = inject(StackFeed);
  readonly config = input<Record<string, unknown>>({});

  readonly rules = computed(() => {
    const loaded = this.feed.alerts.value()?.loaded ?? [];
    return this.config()['firingOnly'] === true ? loaded.filter((r) => r.state === 'firing') : loaded;
  });

  constructor() {
    this.feed.alerts.attach();
  }
}
