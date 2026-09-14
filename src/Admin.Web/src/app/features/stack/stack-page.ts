import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { switchMap, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { OutputPane } from '../../shared/output-pane/output-pane';

@Component({
  selector: 'app-stack-page',
  imports: [FormsModule, OutputPane],
  templateUrl: './stack-page.html',
  styleUrl: './stack-page.css',
  // Angular strips whitespace-only text nodes between non-inline siblings (e.g. adjacent
  // <td>s) by default, which would run the table cells' text together with no separator.
  // The spec asserts on whitespace-collapsed row text, so keep the template's whitespace.
  preserveWhitespaces: true,
})
export class StackPage {
  private readonly host = inject(HostClient);

  /** Polled every 3 seconds; toSignal tears the subscription down with the component. */
  readonly stack = toSignal(timer(0, 3000).pipe(switchMap(() => this.host.stack())));
  readonly config = toSignal(this.host.config());
  readonly jobId = signal<string | null>(null);
  readonly confirmText = signal('');
  readonly canWipe = computed(() => this.confirmText() === 'down -v');
  readonly error = signal<string | null>(null);

  up(): void {
    this.host.backendUp().subscribe(this.started);
  }

  down(): void {
    this.host.backendDown(false).subscribe(this.started);
  }

  wipe(): void {
    if (!this.canWipe()) {
      return;
    }
    this.host.backendDown(true, this.confirmText()).subscribe(this.started);
    this.confirmText.set('');
  }

  private readonly started = {
    next: (job: { id: string }) => {
      this.error.set(null);
      this.jobId.set(job.id);
    },
    error: (e: { error?: { title?: string; detail?: string } }) =>
      this.error.set(e.error?.detail ?? e.error?.title ?? 'The host refused the request.'),
  };
}
