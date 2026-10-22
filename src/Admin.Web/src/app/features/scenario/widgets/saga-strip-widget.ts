import { Component, inject, input } from '@angular/core';
import { ScenarioRunner } from '../scenario-runner';
import { pairingText, sagaText } from '../scenario-run';

/**
 * The Saga strip widget (widget-workspace spec §5.2, spec §5.13): the order's state as the run's watches last read it,
 * the saga's word beside the buyer's, and the transitions, as a view of the one `ScenarioRunner`. It is the only
 * stuck-saga detector a workstation has, so it reads on while the run goes, shown or not.
 */
@Component({
  selector: 'app-saga-strip-widget',
  templateUrl: './saga-strip-widget.html',
  styleUrl: './saga-strip-widget.css',
  preserveWhitespaces: true,
})
export class SagaStripWidget {
  private readonly runner = inject(ScenarioRunner);

  readonly config = input<Record<string, unknown>>({});
  readonly strip = this.runner.strip;
  readonly running = this.runner.running;

  readonly sagaText = sagaText;
  readonly pairingText = pairingText;

  /** Whole seconds in a span of milliseconds. */
  seconds(ms: number): number {
    return Math.round(ms / 1000);
  }

  /** At a time of day on the console's clock, to the second, for the strip's "read at". */
  at(ms: number): string {
    return new Date(ms).toLocaleTimeString('en-GB');
  }
}
