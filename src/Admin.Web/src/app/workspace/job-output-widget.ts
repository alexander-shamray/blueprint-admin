import { Component, DestroyRef, computed, effect, inject, input, viewChild } from '@angular/core';
import { ComposeRun, StackFeed } from '../features/stack/stack-feed';
import { OutputPane } from '../shared/output-pane/output-pane';
import { injectPageJob } from './page-jobs';

/**
 * One job's live output (widget-workspace spec §5.2): the job `config.jobId` names, or, while that is null, the job
 * its page started or opened last. Each instance follows over its own stream (§4.4), so two widgets may show one job.
 */
@Component({
  selector: 'app-job-output-widget',
  imports: [OutputPane],
  template: `
    @if (jobId()) {
      <app-output-pane [jobId]="jobId()" [composeStderr]="compose()" [staircase]="staircase()" [fill]="true" />
    } @else {
      <p class="idle" i18n>No job yet: the output of the next job this page starts shows here.</p>
    }
  `,
  styles: `
    /* The tile's body scrolls what overflows it; the pane fills it instead, so only the pane's log scrolls. */
    :host {
      display: block;
      height: 100%;
    }
    .idle {
      margin: 0;
      color: var(--app-muted);
    }
  `,
})
export class JobOutputWidget {
  private readonly pageJob = injectPageJob().id;
  private readonly run = inject(ComposeRun);
  private readonly feed = inject(StackFeed);
  private readonly pane = viewChild(OutputPane);

  readonly config = input<Record<string, unknown>>({ jobId: null });

  readonly jobId = computed(() => {
    const fixed = this.config()['jobId'];
    return typeof fixed === 'string' ? fixed : this.pageJob();
  });

  /** A `docker compose` job writes its progress to stderr, which the pane then marks only when it says something failed. */
  readonly compose = computed(() => this.run.job()?.id === this.jobId());

  /** Where the stress run is on its staircase when this is its job; the host counts it, so the pane draws what it was told. */
  readonly staircase = computed(() => {
    const run = this.feed.stack.value()?.stressRun;
    return run?.job.id === this.jobId() ? run.progress : null;
  });

  constructor() {
    // The stack is polled for the staircase only once the job has printed a k6 line, so a page of other jobs never asks.
    const destroyRef = inject(DestroyRef);
    let attached = false;
    effect(() => {
      if (attached || !this.pane()?.k6()) return;
      attached = true;
      this.feed.stack.attach(destroyRef);
    });
  }
}
