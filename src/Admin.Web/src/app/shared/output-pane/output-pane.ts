import { Component, ElementRef, computed, effect, inject, input, signal, viewChild, DestroyRef } from '@angular/core';
import { Subscription } from 'rxjs';
import { OutputLine } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';

/**
 * Live output of one job. Give it a job id; it follows until the job exits. The pane is a log a screen reader can
 * read and a keyboard can scroll, but it is not live: Compose prints hundreds of lines, and announcing each would
 * drown everything else. The status beside it says only that a job started and how it ended.
 */
@Component({
  selector: 'app-output-pane',
  template: `
    <p class="visually-hidden" role="status">{{ announcement() }}</p>
    <pre #pane class="pane" role="log" aria-live="off" aria-label="Job output" tabindex="0">@for (l of lines(); track l.sequence) {<span [class.stderr]="l.stream === 'Stderr'">@if (l.stream === 'Stderr') {<span class="tag">[stderr] </span>}{{ l.text }}
</span>}@if (exitCode() !== null) {<span class="exit">exited {{ exitCode() }}</span>}</pre>
    @if (error(); as e) {<p class="error" role="alert">{{ e }}</p>}
  `,
  styles: `
    .pane { max-height: 24rem; overflow: auto; background: #111; color: #ddd; padding: 0.75rem; font-size: 0.8rem; }
    .stderr { color: #f88; }
    .tag { font-weight: 600; }
    .exit { color: #8cf; }
    .error { color: #b00; }
    .visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
  `,
})
export class OutputPane {
  readonly jobId = input<string | null>(null);
  readonly lines = signal<OutputLine[]>([]);
  readonly exitCode = signal<number | null>(null);
  readonly error = signal<string | null>(null);
  readonly announcement = computed(() => {
    if (!this.jobId()) return '';
    const code = this.exitCode();
    return code === null ? 'A job is running; its output is in the job output log.' : `The job exited ${code}.`;
  });

  private readonly sse = inject(SseClient);
  private readonly pane = viewChild<ElementRef<HTMLElement>>('pane');
  private subscription?: Subscription;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());

    effect(() => {
      const id = this.jobId();
      this.subscription?.unsubscribe();
      this.lines.set([]);
      this.exitCode.set(null);
      this.error.set(null);
      if (!id) {
        return;
      }
      this.subscription = this.sse.follow(id).subscribe({
        next: (event) => {
          if (event.kind === 'line') {
            this.lines.update((all) => [...all, event.line]);
          } else {
            this.exitCode.set(event.exitCode);
          }
          queueMicrotask(() => {
            const el = this.pane()?.nativeElement;
            if (el) {
              el.scrollTop = el.scrollHeight;
            }
          });
        },
        error: (err: unknown) => this.error.set(err instanceof Error ? err.message : 'The job stream failed.'),
      });
    });
  }
}
