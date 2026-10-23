import { Component, computed, inject, input } from '@angular/core';
import { ComposeRun } from '../features/stack/stack-feed';
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
      <app-output-pane [jobId]="jobId()" [composeStderr]="compose()" />
    } @else {
      <p class="idle">No job yet: the output of the next job this page starts shows here.</p>
    }
  `,
  styles: `
    .idle {
      margin: 0;
      color: var(--app-muted);
    }
  `,
})
export class JobOutputWidget {
  private readonly pageJob = injectPageJob().id;
  private readonly run = inject(ComposeRun);

  readonly config = input<Record<string, unknown>>({ jobId: null });

  readonly jobId = computed(() => {
    const fixed = this.config()['jobId'];
    return typeof fixed === 'string' ? fixed : this.pageJob();
  });

  /** A `docker compose` job writes its progress to stderr, which the pane then marks only when it says something failed. */
  readonly compose = computed(() => this.run.job()?.id === this.jobId());
}
