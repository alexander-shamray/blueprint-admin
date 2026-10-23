import { Component, computed, inject, input, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { hostErrorText } from '../../../core/host/host-error';
import { DoctorView, JobSummary } from '../../../core/host/host-types';
import { injectPageJob } from '../../../workspace/page-jobs';
import { StackFeed } from '../stack-feed';

/**
 * The reference client (spec §5.4): `npm start` and its stop, and the device lane's build with its three reads. The
 * jobs are read with the stack; a job started or opened here becomes the page's job.
 */
@Component({
  selector: 'app-reference-client-widget',
  templateUrl: './reference-client-widget.html',
  styleUrl: './stack-widgets.css',
  preserveWhitespaces: true,
})
export class ReferenceClientWidget {
  private readonly host = inject(HostClient);
  readonly feed = inject(StackFeed);
  private readonly pageJob = injectPageJob();

  readonly config = input<Record<string, unknown>>({});
  readonly error = signal<string | null>(null);

  /** On error, fall back to undefined, and the missing-node_modules sentence names the clone generically. */
  readonly hostConfig = toSignal(this.host.config().pipe(catchError(() => of(undefined))));

  /** Whether the client's port answers: the `client` reachability entry, which is how spec §5.4's port check reaches here. */
  readonly clientUp = computed(
    () => this.feed.stack.value()?.reachability.find((r) => r.name === 'client')?.up ?? false,
  );
  readonly frontendRunning = computed(() => this.feed.stack.value()?.frontend.job?.state === 'Running');
  readonly deviceBuildRunning = computed(() => this.feed.stack.value()?.frontend.deviceJob?.state === 'Running');

  /**
   * The device lane's three reads (spec §5.4), read when the widget opens and on request. They are reads of the
   * emulator, the gateway and the realm, which a developer makes before pressing the build, and none of them moves by
   * itself; a mismatch is raised in the repository that owns it, not fixed here.
   */
  readonly deviceReads = signal<DoctorView | null>(null);
  readonly deviceReadsError = signal<string | null>(null);
  readonly deviceReading = signal(false);

  constructor() {
    this.feed.stack.attach();
    this.checkDevice();
  }

  checkDevice(): void {
    if (this.deviceReading()) {
      return;
    }

    this.deviceReading.set(true);
    this.host.frontendDeviceReads().subscribe({
      next: (view) => {
        this.deviceReads.set(view);
        this.deviceReadsError.set(null);
        this.deviceReading.set(false);
      },
      error: (e: unknown) => {
        this.deviceReadsError.set(hostErrorText(e));
        this.deviceReading.set(false);
      },
    });
  }

  startFrontend(): void {
    this.host.frontendStart().subscribe(this.started);
  }

  stopFrontend(): void {
    this.host.frontendStop().subscribe(this.started);
  }

  buildForDevice(): void {
    this.host.frontendDeviceBuild().subscribe(this.started);
  }

  showOutput(jobId: string): void {
    this.pageJob.show(jobId);
  }

  private readonly started = {
    next: (job: JobSummary) => {
      this.error.set(null);
      this.pageJob.show(job.id);
    },
    error: (e: unknown) => this.error.set(hostErrorText(e, 'The host refused the request.')),
  };
}
