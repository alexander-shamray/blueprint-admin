import { Component, computed, inject, input } from '@angular/core';
import { pollWhenOn } from '../../../workspace/polled-feed';
import { BrokerFeed, filterOf } from '../broker-feed';

/** Permissions (widget-workspace spec §5.2). Config: a user filter, and the widget's own opt-in auto-refresh. */
@Component({
  selector: 'app-permissions-widget',
  template: `
    <section class="actions">
      <button class="refresh" [disabled]="feed.permissions.refreshing()" (click)="feed.permissions.refresh()"
        >Refresh</button
      >
      @if (autoRefresh()) {
        <span class="auto">Refreshes every 5 s</span>
      }
    </section>
    @if (feed.permissions.error(); as e) {
      <p class="error" role="alert">The host did not answer: {{ e }}</p>
    }
    @if (feed.permissions.value(); as p) {
      @if (p.reachable) {
        <table class="permissions">
          <thead
            ><tr><th>User</th><th>Configure</th><th>Write</th><th>Read</th></tr></thead
          >
          <tbody>
            @for (permission of shown(); track permission.user) {
              <tr>
                <td>{{ permission.user }}</td>
                <td
                  ><code>{{ permission.configure }}</code></td
                >
                <td
                  ><code>{{ permission.write }}</code></td
                >
                <td
                  ><code>{{ permission.read }}</code></td
                >
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <p class="error" role="alert">The broker did not answer: {{ p.error }}</p>
      }
    } @else if (!feed.permissions.error()) {
      <p class="loading">Loading…</p>
    }
  `,
  styleUrl: './broker-widgets.css',
  preserveWhitespaces: true,
})
export class PermissionsWidget {
  readonly feed = inject(BrokerFeed);
  readonly config = input<Record<string, unknown>>({});

  readonly autoRefresh = computed(() => this.config()['autoRefresh'] === true);

  readonly shown = computed(() => {
    const needle = filterOf(this.config());
    return (this.feed.permissions.value()?.permissions ?? []).filter(
      (p) => !needle || p.user.toLowerCase().includes(needle),
    );
  });

  constructor() {
    pollWhenOn(this.feed.permissions, this.autoRefresh);
  }
}
