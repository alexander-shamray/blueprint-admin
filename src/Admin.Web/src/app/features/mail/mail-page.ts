import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, of } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { Mailbox, MailListView, MailMessageView } from '../../core/host/host-types';

/**
 * What the platform sent, read from the mail sink through the host (spec §5.12). The screen only reads: clearing the
 * sink is done in its own UI, which the screen links, and the sink's chaos API is never called from here.
 */
@Component({
  selector: 'app-mail-page',
  imports: [FormsModule, RouterLink],
  templateUrl: './mail-page.html',
  styleUrl: './mail-page.css',
  preserveWhitespaces: true,
})
export class MailPage {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly sanitizer = inject(DomSanitizer);

  readonly list = signal<MailListView | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  /** One order's messages: those whose `X-Correlation-Id` is this. Empty shows every message. */
  readonly filter = signal(inject(ActivatedRoute).snapshot.queryParamMap.get('correlationId') ?? '');
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
   * inline styles, which is the rendering the screen exists to show, so it is trusted here and nowhere else.
   */
  readonly html = computed<SafeHtml | null>(() => {
    const html = this.opened()?.html ?? '';
    return html ? this.sanitizer.bypassSecurityTrustHtml(html) : null;
  });

  constructor() {
    this.refresh();
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
    this.host
      .mailMessage(id)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        catchError(() => of({ reachable: false, error: 'The host did not answer.', message: null })),
      )
      .subscribe((read) => {
        this.opened.set(read.message);
        if (read.message === null) this.openError.set(read.reachable ? read.error : `The mail sink did not answer: ${read.error}`);
      });
  }

  close(): void {
    this.opened.set(null);
    this.openError.set(null);
  }

  mailboxes(boxes: Mailbox[]): string {
    return boxes.map((b) => (b.name ? `${b.name} <${b.address}>` : b.address)).join(', ');
  }
}
