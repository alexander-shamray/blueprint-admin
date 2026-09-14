import { Component, ElementRef, effect, inject, input, signal, viewChild, DestroyRef } from '@angular/core';
import { Subscription } from 'rxjs';
import { OutputLine } from '../../core/host/host-types';
import { SseClient } from '../../core/host/sse-client';

/** Live output of one job. Give it a job id; it follows until the job exits. */
@Component({
  selector: 'app-output-pane',
  template: `
    <pre #pane class="pane">@for (l of lines(); track l.sequence) {<span [class.stderr]="l.stream === 'Stderr'">@if (l.stream === 'Stderr') {<span class="tag">[stderr] </span>}{{ l.text }}
</span>}@if (exitCode() !== null) {<span class="exit">exited {{ exitCode() }}</span>}</pre>
    @if (error(); as e) {<p class="error">{{ e }}</p>}
  `,
  styles: `
    .pane { max-height: 24rem; overflow: auto; background: #111; color: #ddd; padding: 0.75rem; font-size: 0.8rem; }
    .stderr { color: #f88; }
    .tag { font-weight: 600; }
    .exit { color: #8cf; }
    .error { color: #b00; }
  `,
})
export class OutputPane {
  readonly jobId = input<string | null>(null);
  readonly lines = signal<OutputLine[]>([]);
  readonly exitCode = signal<number | null>(null);
  readonly error = signal<string | null>(null);

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
