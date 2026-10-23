import { Component, DestroyRef, computed, effect, inject, input, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { OutputLine } from '../../../core/host/host-types';
import { injectWidgetContext } from '../../../workspace/widget-context';
import { shows, stringList } from '../../../workspace/widget-config';
import { widgetSession } from '../../../workspace/widget-session-store';
import { COMPOSE_SERVICES } from '../compose-services';
import { LogFollowService, lineService } from '../log-follow';

let anonymous = 0;

/**
 * Logs (widget-workspace spec §4.4, §5.2): a view of the one follow `LogFollowService` runs, filtered to the services
 * this widget is configured for (`[]` is every one), its text and a correlation id. Follow starts the shared follow
 * over every Logs widget's services, so two widgets with different services each get their own lines from one job.
 * Leaving the page leaves the follow running; Stop is what ends it.
 */
@Component({
  selector: 'app-logs-widget',
  imports: [FormsModule],
  templateUrl: './logs-widget.html',
  styleUrl: './logs-widget.css',
})
export class LogsWidget {
  readonly follow = inject(LogFollowService);
  readonly config = input<Record<string, unknown>>({});

  /** What the operator typed into the two boxes: session state (§4.2), found again when the widget is re-created. */
  readonly filter = widgetSession('filter', '');
  readonly correlationId = widgetSession('correlationId', '');

  /** The services this widget shows; `[]` is every one. */
  readonly services = computed(() => stringList(this.config()['services']));

  /** Configured services Compose has none of: said, because such a widget would wait for lines that never come. */
  readonly unknown = computed(() => this.services().filter((s) => !COMPOSE_SERVICES.includes(s)));

  readonly visible = computed(() => {
    const services = this.services();
    const needle = this.filter().toLowerCase();
    return this.follow.lines().filter((l) => {
      const service = lineService(l.text);
      const own = services.length === 0 || (service !== null && shows(services, service));
      return own && (!needle || l.text.toLowerCase().includes(needle));
    });
  });

  constructor() {
    const key = injectWidgetContext()?.instanceId ?? `logs-anonymous-${++anonymous}`;
    // Untracked: `ask` reads the service's signals to decide on a restart, and one widget's write must not wake another's.
    effect(() => {
      const services = this.services();
      untracked(() => this.follow.ask(key, services));
    });
    inject(DestroyRef).onDestroy(() => this.follow.withdraw(key));
  }

  isHit(line: OutputLine): boolean {
    const id = this.correlationId();
    return id.length > 0 && line.text.includes(id);
  }
}
