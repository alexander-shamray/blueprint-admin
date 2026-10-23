import { Component, DestroyRef, effect, inject, input, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DrainedIndicator } from '../../../shared/drained-indicator/drained-indicator';
import { pretty } from '../../api/request-builder';
import { TraceLink } from '../../trace/trace-follow';
import { ScenarioLauncher } from '../scenario-launcher';
import { ScenarioRunner, StepState } from '../scenario-runner';

/** Each state's word, because state is never carried by colour alone. */
const STATE_WORDS: Record<StepState, string> = {
  pending: '[pending]',
  running: '[running]',
  ok: '[ok]',
  failed: '[failed]',
  skipped: '[not run]',
};

/**
 * The Scenario widget (widget-workspace spec §5.2): the script, who it runs as, Run, Cancel and Export, and the run's
 * steps, as a view of the one `ScenarioRunner`. Two Scenario widgets show the same run, and Run is refused while it
 * goes; leaving the page destroys this view and leaves the run going.
 */
@Component({
  selector: 'app-scenario-widget',
  imports: [FormsModule, TraceLink, DrainedIndicator],
  templateUrl: './scenario-widget.html',
  styleUrl: './scenario-widget.css',
  preserveWhitespaces: true,
})
export class ScenarioWidget {
  readonly runner = inject(ScenarioRunner);
  private readonly launcher = inject(ScenarioLauncher);

  readonly config = input<Record<string, unknown>>({});

  constructor() {
    // A request this view never acted on would otherwise start a run on the next visit, unasked.
    inject(DestroyRef).onDestroy(() => this.launcher.take());
    this.runner.load();
    // The palette's "Run" waits for the catalog and a user, as the button does; one asked for during a run is dropped
    // rather than queued behind it, so a second keystroke never orders twice.
    effect(() => {
      if (!this.launcher.pending()) return;
      const running = this.runner.running();
      const canRun = this.runner.canRun();
      untracked(() => {
        // This view is here to take the request, so it waits for the catalog and a user however long they take,
        // whether it asked before the view was created or while it was open, rather than lapsing.
        this.launcher.hold();
        if (running) this.launcher.take();
        else if (canRun && this.launcher.take()) void this.runner.run();
      });
    });
  }

  stateWord(state: StepState): string {
    return STATE_WORDS[state];
  }

  prettyBody(body: string): string {
    return pretty(body);
  }
}
