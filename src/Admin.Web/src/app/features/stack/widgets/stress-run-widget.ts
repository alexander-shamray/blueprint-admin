import { Component, computed, inject, input, signal } from '@angular/core';
import { HostClient } from '../../../core/host/host-client';
import { hostErrorText } from '../../../core/host/host-error';
import { injectPageJob } from '../../../workspace/page-jobs';
import { ComposeRun, StackFeed } from '../stack-feed';

/**
 * The backend's stress run (spec §5.3): a staircase of signed-in users through the gateway until the platform gives out,
 * on a stack brought up with the stress override. The last run, its step and its knee are read with the stack, and so is
 * the override, from the containers, so a stack left on it says so here whoever left it. The way back is the Up, which is
 * the section's own last block.
 */
@Component({
  selector: 'app-stress-run-widget',
  template: `
    <section class="stress-run" i18n-aria-label aria-label="Stress run">
      <p class="stress-run-status">
        <ng-container i18n>k6 run stress.js</ng-container>&ngsp;
        @if (stressRun(); as r) {
          @if (r.job.state === 'Running') {
            <ng-container i18n>running</ng-container>
          } @else {
            <ng-container i18n>exited {{ r.job.exitCode }}</ng-container>
          }
        } @else if (feed.stack.value()) {
          <ng-container i18n>not run</ng-container>
        }
      </p>
      @if (!feed.stack.value() && feed.stack.error(); as e) {
        <p class="error" i18n>The host did not answer: {{ e }}</p>
      }
      @if (running()) {
        <p class="stress-run-step">
          @if (stressRun()?.progress; as p) {
            @if (p.holding) {
              <ng-container i18n
                >Step {{ p.step }}: holding at {{ p.users }} of {{ p.maxUsers }} users, {{ p.elapsed }} in</ng-container
              >
            } @else if (p.users === 0 && p.step > 0) {
              <ng-container i18n
                >k6 has stopped after step {{ p.step }}; the sampler and summarise.mjs are writing the
                summary.</ng-container
              >
            } @else if (p.step === 0) {
              <ng-container i18n
                >Ramping to the first step: {{ p.users }} of {{ p.maxUsers }} users, {{ p.elapsed }} in</ng-container
              >
            } @else {
              <ng-container i18n
                >Ramping on from step {{ p.step }}: {{ p.users }} of {{ p.maxUsers }} users,
                {{ p.elapsed }} in</ng-container
              >
            }
          } @else {
            <ng-container i18n
              >Before k6: the up with the override, a minute idle and Prometheus's read come first.</ng-container
            >
          }
        </p>
      }
      @if (stressRun()?.knee; as knee) {
        <p class="stress-run-knee">{{ knee }}</p>
      }
      @if (overridden().length > 0) {
        <p class="stress-override" role="status" i18n>
          The stack runs on the stress override ({{ overridden().join(', ') }}): the gateway's rate limits are lifted
          and the workers do not pace themselves. It is never for a deployment.
        </p>
        <button
          class="stress-restore"
          type="button"
          [disabled]="running() || compose.running()"
          (click)="restore()"
          i18n-title
          title="Up without the override, run-locally.md's way back: it recreates the gateway and both workers with every default"
          i18n
          >Back to the normal profile</button
        >
      }
      <p class="stress-run-note" i18n>
        The backend's stress run: distinct signed-in users walk the shop's typical visit through the gateway, more of
        them at each step, until the platform gives out. It first brings the stack up with the stress override, which
        recreates the gateway and both workers, and takes about twenty minutes. It reports and never passes or fails. A
        local run says which part gives out first and roughly where, never what production can carry.
      </p>
      @if (error(); as e) {
        <p class="error">{{ e }}</p>
      }
      <button class="stress-run-start" type="button" [disabled]="running()" (click)="start()" i18n>Run stress</button>
      <button
        class="stress-run-stop"
        type="button"
        [disabled]="!running()"
        (click)="stop()"
        i18n-title
        title="Ends the step going at once, and during k6 no summary is written, so there is no knee and no verdict; once k6 has ended it waits for the summary first"
        i18n
        >Stop stress</button
      >
      @if (stressRun(); as r) {
        <button class="stress-run-output" type="button" (click)="pageJob.show(r.job.id)" i18n
          >Show stress output</button
        >
      }
    </section>
  `,
  styleUrl: './stack-widgets.css',
  // The buttons are inline and spaced by the template's own whitespace, as the load run's are.
  preserveWhitespaces: true,
})
export class StressRunWidget {
  private readonly host = inject(HostClient);
  readonly feed = inject(StackFeed);
  readonly compose = inject(ComposeRun);
  readonly pageJob = injectPageJob();

  readonly config = input<Record<string, unknown>>({});
  readonly error = signal<string | null>(null);

  readonly stressRun = computed(() => this.feed.stack.value()?.stressRun ?? null);
  readonly running = computed(() => this.stressRun()?.job.state === 'Running');
  readonly overridden = computed(() => this.feed.stack.value()?.stressOverride ?? []);

  constructor() {
    this.feed.stack.attach();
  }

  start(): void {
    this.host.stressRunStart().subscribe({
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
      .stressRunStop()
      .subscribe({ error: (e: unknown) => this.error.set(hostErrorText(e, $localize`The host refused the request.`)) });
  }

  /** The Stack's Up, named as the Stack controls name it, so their buttons wait for it as for their own. */
  restore(): void {
    this.host.backendUp().subscribe({
      next: (job) => {
        this.error.set(null);
        this.compose.started(job.id, $localize`Up`);
        this.pageJob.show(job.id);
      },
      error: (e: unknown) => this.error.set(hostErrorText(e, $localize`The host refused the request.`)),
    });
  }
}
