import { Component, DestroyRef, computed, effect, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { widgetSession } from '../../../workspace/widget-session-store';
import { TraceLink } from '../../trace/trace-follow';
import { ErasureRun, GUID } from '../erasure-run';

/**
 * Raises an erasure request through the gateway as the `privacy` login and shows each holder answering it (spec §5.13).
 * The subject being typed is this widget's session state (§4.2); the request being watched, what it last read and the
 * every-3-seconds watch belong to `ErasureRun`, so leaving the page and coming back finds them.
 */
@Component({
  selector: 'app-erasure-widget',
  imports: [FormsModule, TraceLink],
  templateUrl: './erasure-widget.html',
  styleUrl: './erasure-widget.css',
  preserveWhitespaces: true,
})
export class ErasureWidget {
  readonly run = inject(ErasureRun);

  readonly subject = widgetSession('subject', '');
  readonly canRaise = computed(() => GUID.test(this.subject().trim()) && !this.run.busy());

  constructor() {
    const destroyRef = inject(DestroyRef);
    effect((onCleanup) => {
      if (this.run.watch()) onCleanup(this.run.watchFeed.attach(destroyRef));
    });
  }

  newSubject(): void {
    this.subject.set(crypto.randomUUID());
  }

  raise(): void {
    if (this.canRaise()) this.run.raise(this.subject());
  }
}
