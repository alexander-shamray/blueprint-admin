import { Component, computed, inject, input } from '@angular/core';
import { DrainedIndicator } from '../../../shared/drained-indicator/drained-indicator';
import { BrokerFeed, filterOf, listFeed } from '../broker-feed';

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
      <button class="refresh" [disabled]="feed.queues.refreshing()" (click)="feed.queues.refresh()">Refresh</button>
      @if (autoRefresh()) {
        <span class="auto">Refreshes every 5 s</span>
      }
    </section>
    @if (feed.queues.error(); as e) {
      <p class="error" role="alert">The host did not answer: {{ e }}</p>
    }
    @if (feed.queues.value(); as q) {
      @if (q.reachable) {
        <app-drained-indicator [projection]="q.projection" />
        <table class="queues">
          <thead><tr><th>Queue</th><th>Messages</th><th>Consumed by</th><th>Runbook</th><th>Dead letters</th></tr></thead>
          <tbody>
            @for (queue of shown(); track queue.name) {
              <tr [class.error-queue]="queue.parked === 'error'" [class.skipped-queue]="queue.parked === 'skipped'">
                <td>@if (queue.parked; as kind) {<span class="marker">[{{ kind }}]</span> }{{ queue.name }}</td>
                <td>{{ queue.messages ?? '—' }}</td>
                <td class="consumer">{{ queue.consumer ?? '' }}</td>
                <td class="runbook">@if (queue.runbook && (queue.messages ?? 0) > 0) {<a [href]="queue.runbook" target="_blank" rel="noopener noreferrer">{{ queue.parked }}-queue runbook</a>}</td>
                <td class="open">@if (queue.parked && feed.deadLetterPlan() && feed.workbenches() > 0 && (queue.messages ?? 0) > 0) {<button class="open-dead-letters" (click)="feed.requestOpen(queue.name)" [attr.aria-label]="'Open the messages on ' + queue.name">Open</button>}</td>
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <p class="error" role="alert">The broker did not answer: {{ q.error }}</p>
      }
    } @else if (!feed.queues.error()) {
      <p class="loading">Loading…</p>
    }
  `,
  styleUrl: './broker-widgets.css',
  // Keep the whitespace between adjacent <td>s so row text reads as separate cells, as the Stack screen does.
  preserveWhitespaces: true,
})
export class QueuesWidget {
  readonly feed = inject(BrokerFeed);
  readonly config = input<Record<string, unknown>>({});

  readonly autoRefresh = computed(() => this.config()['autoRefresh'] === true);

  /** The queues the filter and "parked only" leave. */
  readonly shown = computed(() => {
    const needle = filterOf(this.config());
    const parkedOnly = this.config()['parkedOnly'] === true;
    return (this.feed.queues.value()?.queues ?? []).filter(
      (q) => (!needle || q.name.toLowerCase().includes(needle)) && (!parkedOnly || q.parked !== null),
    );
  });

  constructor() {
    listFeed(this.feed.queues, this.autoRefresh);
  }
}
