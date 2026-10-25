import { Component, computed, inject, input, signal } from '@angular/core';
import { HostClient } from '../../../core/host/host-client';
import { hostErrorText } from '../../../core/host/host-error';
import { injectPageJob } from '../../../workspace/page-jobs';
import { StackFeed } from '../stack-feed';

/**
 * The backend's SLO run (spec §5.3), so the golden signals are read under load. The last run is read with the stack,
 * so a run started elsewhere or before a reload is shown; a run started here becomes the page's job.
 */
@Component({
  selector: 'app-load-run-widget',
  template: `
    <section class="load-run" i18n-aria-label aria-label="Load run">
      <p class="load-run-status">
        <ng-container i18n>k6 run slo.js</ng-container>&ngsp;
        @if (loadRun(); as job) {
          @if (job.state === 'Running') {
            <ng-container i18n>running</ng-container>
          } @else {
            <ng-container i18n>exited {{ job.exitCode }}</ng-container>
          }
        } @else if (feed.stack.value()) {
          <ng-container i18n>not run</ng-container>
        }
      </p>
      @if (!feed.stack.value() && feed.stack.error(); as e) {
        <p class="error" i18n>The host did not answer: {{ e }}</p>
      }
      <p class="load-run-note" i18n>
        The backend's SLO run, so the golden signals are read under load: it places orders and lists products through
        the gateway for a few minutes. A local run is not a staging baseline and proves nothing about the SLO (§13.7).
        Its orders, the slo-run client and the restocked count are left as they are (run-locally.md, Load run (k6),
        Afterwards).
      </p>
      @if (error(); as e) {
        <p class="error">{{ e }}</p>
      }
      <button class="load-run-start" type="button" [disabled]="running()" (click)="start()" i18n>Run load</button>
      <button
        class="load-run-stop"
        type="button"
        [disabled]="!running()"
        (click)="stop()"
        i18n-title
        title="Ends the step going at once; during k6, teardown() does not run, so there is no verdict"
        i18n
        >Stop load</button
      >
      @if (loadRun(); as job) {
        <button class="load-run-output" type="button" (click)="pageJob.show(job.id)" i18n>Show load output</button>
      }
    </section>
  `,
  styleUrl: './stack-widgets.css',
  // The buttons are inline and spaced by the template's own whitespace, as they were on the Stack screen.
  preserveWhitespaces: true,
})
export class LoadRunWidget {
  private readonly host = inject(HostClient);
  readonly feed = inject(StackFeed);
  readonly pageJob = injectPageJob();

  readonly config = input<Record<string, unknown>>({});
  readonly error = signal<string | null>(null);

  readonly loadRun = computed(() => this.feed.stack.value()?.loadRun ?? null);
  readonly running = computed(() => this.loadRun()?.state === 'Running');

  constructor() {
    this.feed.stack.attach();
  }

  start(): void {
    this.host.loadRunStart().subscribe({
      next: (job) => {
        this.error.set(null);
        this.pageJob.show(job.id);
      },
      error: (e: unknown) => this.error.set(hostErrorText(e, $localize`The host refused the request.`)),
    });
  }

  /** Stop answers once the run has ended, so its output is already showing; only a refusal needs saying. */
  stop(): void {
    this.host
      .loadRunStop()
      .subscribe({ error: (e: unknown) => this.error.set(hostErrorText(e, $localize`The host refused the request.`)) });
  }
}
