import { DestroyRef, Directive, Injectable, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';

/**
 * "Follow the last Trace link" (widget-workspace spec §5.2): a correlation-id link in any widget re-points the Trace
 * widgets on the page that follow, and opens the built-in Trace page when there is none. Root-provided and in memory.
 * The widgets of a page that is not showing are destroyed, so a follower counted here is always on the page the
 * operator is looking at, which is the page a link was clicked on.
 */
@Injectable({ providedIn: 'root' })
export class TraceFollow {
  private readonly router = inject(Router);

  /** How many Trace widgets that follow are showing. */
  readonly followers = signal(0);

  /** The correlation id a link last pointed the followers at, counted so the same id clicked twice is two points. */
  readonly pointed = signal<{ id: string; n: number } | null>(null);

  /** A Trace widget that follows is showing; the function it returns, or its destruction, ends that. */
  follow(destroyRef: DestroyRef): () => void {
    this.followers.update((n) => n + 1);
    let released = false;
    const release = (): void => {
      if (released) return;
      released = true;
      this.followers.update((n) => n - 1);
    };
    destroyRef.onDestroy(release);
    return release;
  }

  /** Re-points the Trace widgets that follow at `id`. */
  point(id: string): void {
    this.pointed.update((last) => ({ id, n: (last?.n ?? 0) + 1 }));
  }

  /** What a link to a trace does: re-point the followers on the page, or open the Trace page when there are none. */
  open(id: string): void {
    if (this.followers() > 0) this.point(id);
    else void this.router.navigate(['/trace', id]);
  }
}

/**
 * A link to one correlation id's trace, for any widget: `<a [appTraceLink]="id">`. Its address is the built-in Trace
 * page's, so it opens in a new tab or window as an ordinary link does; a plain click goes through `TraceFollow.open`.
 */
@Directive({
  selector: 'a[appTraceLink]',
  host: {
    '[attr.href]': 'href()',
    '(click)': 'clicked($event)',
  },
})
export class TraceLink {
  private readonly follow = inject(TraceFollow);

  readonly appTraceLink = input.required<string>();

  href(): string {
    return `/trace/${encodeURIComponent(this.appTraceLink())}`;
  }

  clicked(event: MouseEvent): void {
    // A modified click or another button is the browser's own: a new tab, a download.
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    this.follow.open(this.appTraceLink());
  }
}
