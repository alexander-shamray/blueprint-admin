import { Component, computed, inject, input } from '@angular/core';
import { shows, stringList } from '../../../workspace/widget-config';
import { StackFeed } from '../stack-feed';

/** Compose's own view of each platform service (spec §5.3): `docker compose ps`, read with the stack. */
@Component({
  selector: 'app-compose-services-widget',
  template: `
    @if (feed.stack.error(); as e) {
      <p class="error">The host did not answer: {{ e }}</p>
    }
    @if (feed.stack.value(); as s) {
      @if (!s.backend.reachable) {
        <p class="error">Docker did not answer: {{ s.backend.error }}</p>
      }
      <table class="services">
        <thead
          ><tr><th>Service</th><th>State</th><th>Health</th><th>Ports</th></tr></thead
        >
        <tbody>
          @for (svc of services(); track svc.service) {
            <tr [class.exited]="svc.state === 'exited'">
              <td>{{ svc.service }}</td>
              <td>{{ svc.state }}</td>
              <td>{{ svc.health ?? '—' }}</td>
              <td>{{ svc.publishedPorts.length ? svc.publishedPorts.join(', ') : '—' }}</td>
            </tr>
          }
        </tbody>
      </table>
    } @else if (!feed.stack.error()) {
      <p class="loading">Loading…</p>
    }
  `,
  styleUrl: './stack-widgets.css',
  preserveWhitespaces: true,
})
export class ComposeServicesWidget {
  readonly feed = inject(StackFeed);
  readonly config = input<Record<string, unknown>>({});

  /** The services `config.services` names, or every one when it names none. */
  readonly services = computed(() => {
    const shown = stringList(this.config()['services']);
    return (this.feed.stack.value()?.backend.services ?? []).filter((s) => shows(shown, s.service));
  });

  constructor() {
    this.feed.stack.attach();
  }
}
