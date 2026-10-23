import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { ActivatedRoute } from '@angular/router';
import { catchError, of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { Mailbox, MailListView, MailMessageView } from '../../../core/host/host-types';
import { widgetSession } from '../../../workspace/widget-session-store';
import { TraceLink } from '../../trace/trace-follow';

/**
 * What the platform sent, read from the mail sink through the host (spec §5.12). The widget only reads: clearing the
 * sink is done in its own UI, which the widget links, and the sink's chaos API is never called from here. Config:
 * `correlationId`, the filter the widget starts with (the settings editor sets it), over which an old link's
 * `?correlationId=` wins. What the operator types over it, and the id of the message that is open (read again when the
 * widget is re-created), are session state (§4.2): they outlive the widget's component and end with the tab.
 */
@Component({
  selector: 'app-mail-widget',
  imports: [FormsModule, TraceLink],
  templateUrl: './mail-widget.html',
  styleUrl: './mail-widget.css',
  preserveWhitespaces: true,
})
export class MailWidget {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly sanitizer = inject(DomSanitizer);

  readonly config = input<Record<string, unknown>>({});

  readonly list = signal<MailListView | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  /** The `?correlationId=` the route carries, which an old link (a trace's, or a bookmark) hands the widget (§5.3). */
  private readonly fromRoute =
    inject(ActivatedRoute, { optional: true })?.snapshot.queryParamMap.get('correlationId') ?? '';
  private readonly configured = computed(() => {
    const configured = this.config()['correlationId'];
    return typeof configured === 'string' ? configured : '';
  });

  /**
   * What the operator typed in the filter box, or null while they have not: the route's, or else the config's, filter
   * shows until then. Choosing another configured filter (the settings editor, a preset) takes the typed one away.
   */
  private readonly typed = widgetSession<string | null>('filter', null);
  /** The configured filter that was showing when it was typed over, so a change made while the widget was gone is seen. */
  private readonly typedAgainst = widgetSession<string>('filterAgainst', '');
  /** One order's messages: those whose `X-Correlation-Id` is this. Empty shows every message. */
  readonly filter = computed(() => this.typed() ?? (this.fromRoute || this.configured()));
  /** The message that is open: its id is session state, and it is read again when the widget is re-created. */
  private readonly openedId = widgetSession<string | null>('openedId', null);
  readonly opened = signal<MailMessageView | null>(null);
  readonly openError = signal<string | null>(null);

  readonly messages = computed(() => {
    const wanted = this.filter().trim();
    const all = this.list()?.messages ?? [];
    return wanted ? all.filter((m) => m.correlationId === wanted) : all;
  });

  /** True when no listed message carries a correlation id, which is what the backend's mail lacked before it sent one. */
  readonly noneCorrelated = computed(() => {
    const all = this.list()?.messages ?? [];
    return all.length > 0 && all.every((m) => m.correlationId === null);
  });

  /**
   * The HTML part, for a frame with `sandbox` and no `allow-scripts`: the frame's sandbox is what keeps the template's
   * markup from running or reaching this origin. Angular's own sanitising would strip the template's `<style>` and
   * inline styles, which is the rendering the widget exists to show, so it is trusted here and nowhere else.
   */
  readonly html = computed<SafeHtml | null>(() => {
    const html = this.opened()?.html ?? '';
    return html ? this.sanitizer.bypassSecurityTrustHtml(html) : null;
  });

  constructor() {
    this.refresh();
    // An old link's filter wins over one typed earlier in the tab: the link is the newest thing the operator asked for.
    if (this.fromRoute) this.typed.set(null);
    const id = this.openedId();
    if (id !== null) this.open(id);
    // Choosing another configured filter takes a typed one away, whether it is chosen while the widget shows or while it
    // is gone (its inputs arrive after the constructor, so the effect is where the configured filter is first known).
    effect(() => {
      const now = this.configured();
      untracked(() => {
        if (this.typed() !== null && this.typedAgainst() !== now) this.typed.set(null);
      });
    });
  }

  setFilter(value: string): void {
    this.typedAgainst.set(this.configured());
    this.typed.set(value);
  }

  refresh(): void {
    this.loading.set(true);
    this.host
      .mailMessages()
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        catchError(() => {
          this.error.set('The host did not answer.');
          this.loading.set(false);
          return of(null);
        }),
      )
      .subscribe((view) => {
        if (view === null) return;
        this.list.set(view);
        this.error.set(view.reachable ? null : `The mail sink did not answer: ${view.error}`);
        this.loading.set(false);
      });
  }

  open(id: string): void {
    this.openError.set(null);
    this.openedId.set(id);
    this.host
      .mailMessage(id)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        catchError(() => of({ reachable: false, error: 'The host did not answer.', message: null })),
      )
      .subscribe((read) => {
        // The answer is for the message last asked for: a Close, or another Open, since has made it stale.
        if (this.openedId() !== id) return;
        this.opened.set(read.message);
        if (read.message === null) {
          // The id is dropped only when the sink answered that the message is gone; a read that failed in transit
          // leaves it, so the next time the widget is created it tries again.
          if (read.reachable) this.openedId.set(null);
          this.openError.set(read.reachable ? read.error : `The mail sink did not answer: ${read.error}`);
        }
      });
  }

  close(): void {
    this.opened.set(null);
    this.openedId.set(null);
    this.openError.set(null);
  }

  mailboxes(boxes: Mailbox[]): string {
    return boxes.map((b) => (b.name ? `${b.name} <${b.address}>` : b.address)).join(', ');
  }
}
