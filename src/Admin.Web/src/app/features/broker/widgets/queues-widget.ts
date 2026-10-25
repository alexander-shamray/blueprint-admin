import { Component, computed, inject, input } from '@angular/core';
import { DrainedIndicator } from '../../../shared/drained-indicator/drained-indicator';
import { pollWhenOn } from '../../../workspace/polled-feed';
import { widgetSession } from '../../../workspace/widget-session-store';
import { BrokerFeed, filterOf } from '../broker-feed';

/**
 * Queues (widget-workspace spec §5.2): each queue's depth, the parked ones marked in words with the runbook of one
 * that holds a message, and the ordering-catalog-events indicator. Config: a name filter, parked queues only, and
 * the widget's own opt-in auto-refresh (off unless switched on).
 */
@Component({
  selector: 'app-queues-widget',
  imports: [DrainedIndicator],
  template: `
    <section class="actions">
      <button class="refresh" [disabled]="feed.queues.refreshing()" (click)="feed.queues.refresh()" i18n
        >Refresh</button
      >
      @if (autoRefresh()) {
        <span class="auto" i18n>Refreshes every 5 s</span>
      }
    </section>
    @if (pointed(); as name) {
      <p class="pointed" role="status">
        <ng-container i18n>Pointed at {{ name }} from the platform diagram.</ng-container>&ngsp;<button
          class="unpoint"
          type="button"
          (click)="pointed.set(null)"
          i18n
          >Show the configured queues</button
        >
      </p>
    }
    @if (feed.queues.error(); as e) {
      <p class="error" role="alert" i18n>The host did not answer: {{ e }}</p>
    }
    @if (feed.queues.value(); as q) {
      @if (q.reachable) {
        <app-drained-indicator [projection]="q.projection" />
        <table class="queues">
          <thead
            ><tr
              ><th i18n>Queue</th><th i18n>Messages</th><th i18n>Consumed by</th><th i18n>Runbook</th
              ><th i18n>Dead letters</th></tr
            ></thead
          >
          <tbody>
            @for (queue of shown(); track queue.name) {
              <tr [class.error-queue]="queue.parked === 'error'" [class.skipped-queue]="queue.parked === 'skipped'">
                <td>
                  @if (queue.parked; as kind) {
                    <span class="marker">[{{ kind }}]</span>
                  }
                  {{ queue.name }}</td
                >
                <td>{{ queue.messages ?? '—' }}</td>
                <td class="consumer">{{ queue.consumer ?? '' }}</td>
                <td class="runbook">
                  @if (queue.runbook && (queue.messages ?? 0) > 0) {
                    <a [href]="queue.runbook" target="_blank" rel="noopener noreferrer" i18n
                      >{{ queue.parked }}-queue runbook</a
                    >
                  }
                </td>
                <td class="open">
                  @if (queue.parked && feed.deadLetterPlan() && feed.workbenches() > 0 && (queue.messages ?? 0) > 0) {
                    <button
                      class="open-dead-letters"
                      (click)="feed.requestOpen(queue.name)"
                      [attr.aria-label]="openLabel(queue.name)"
                      i18n
                      >Open</button
                    >
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <p class="error" role="alert" i18n>The broker did not answer: {{ q.error }}</p>
      }
    } @else if (!feed.queues.error()) {
      <p class="loading" i18n>Loading…</p>
    }
  `,
  styleUrl: './broker-widgets.css',
  // Keep the whitespace between adjacent <td>s so row text reads as separate cells, as the Stack screen does.
  preserveWhitespaces: true,
})
export class QueuesWidget {
  readonly feed = inject(BrokerFeed);
  readonly config = input<Record<string, unknown>>({});

  /** The Open button's accessible name, with its queue. */
  openLabel = (queue: string): string => $localize`Open the messages on ${queue}:queue:`;

  readonly autoRefresh = computed(() => this.config()['autoRefresh'] === true);

  /**
   * The queue the platform diagram pointed this widget at (spec §8.1): a name filter over the configured one, and
   * "parked only" is set aside, since the queue asked for may not be parked. Session state (§4.2): pointing is not a
   * change to the saved page, and the operator takes it off to get the configured view back.
   */
  readonly pointed = widgetSession<string | null>('pointed', null);

  /** The queues the filter and "parked only" leave. */
  readonly shown = computed(() => {
    const pointed = this.pointed();
    const needle = pointed === null ? filterOf(this.config()) : pointed.toLowerCase();
    const parkedOnly = pointed === null && this.config()['parkedOnly'] === true;
    return (this.feed.queues.value()?.queues ?? []).filter(
      (q) => (!needle || q.name.toLowerCase().includes(needle)) && (!parkedOnly || q.parked !== null),
    );
  });

  constructor() {
    pollWhenOn(this.feed.queues, this.autoRefresh);
  }
}
