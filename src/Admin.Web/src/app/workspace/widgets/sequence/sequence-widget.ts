import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { EMPTY, Subject, catchError, defer, finalize, switchMap } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { TraceView } from '../../../core/host/host-types';
import { TraceFollow } from '../../../features/trace/trace-follow';
import { TraceDiff, TraceHistory, diffTimelines } from '../../../features/trace/trace-history';
import { WINDOWS } from '../../../features/trace/widgets/trace-widget';
import { widgetSession } from '../../widget-session-store';
import { MermaidDiagram } from '../note/mermaid-diagram';
import { sequenceOf } from './sequence-source';

/**
 * Where one request went and which hop was slow (widget-workspace spec §8.2): a Mermaid sequence diagram of the Trace
 * timeline, read through the same endpoint and kept in the same `TraceHistory`, so no host read is new. Config:
 * `correlationId` and `window` to start with, and `follow`, which re-points it at the last Trace link. It is drawn when
 * asked (Draw, Reload, a configured or linked id) and never polled. The id last drawn and the window chosen are session
 * state (§4.2). Each arrow is numbered, and the numbered list under the diagram links its span in Grafana Explore.
 */
@Component({
  selector: 'app-sequence-widget',
  imports: [DatePipe, FormsModule, MermaidDiagram],
  templateUrl: './sequence-widget.html',
  styleUrl: './sequence-widget.css',
})
export class SequenceWidget {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly history = inject(TraceHistory);
  private readonly follow = inject(TraceFollow);

  readonly config = input<Record<string, unknown>>({});

  readonly windows = WINDOWS;
  readonly view = signal<TraceView | null>(null);
  /** What moved since this session's previous fetch of the id on screen; null on its first fetch. */
  readonly diff = signal<TraceDiff | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly correlationId = signal('');
  /** The window the operator chose, or '' while they have not: session state (§4.2), over the configured one. */
  readonly window = widgetSession('window', '');
  readonly shownWindow = computed(() => this.window() || this.configuredWindow());

  /** The id last drawn, kept for the tab so a re-created widget draws it again. */
  private readonly drawn = widgetSession('traced', '');

  private readonly configuredId = computed(() => {
    const id = this.config()['correlationId'];
    return typeof id === 'string' ? id.trim() : '';
  });
  private readonly configuredWindow = computed(() => {
    const w = this.config()['window'];
    return WINDOWS.find((x) => x === w) ?? WINDOWS[0];
  });
  readonly following = computed(() => this.config()['follow'] === true);

  /** The diagram of the timeline on screen; null while there is none, or when it holds nothing to draw. */
  readonly sequence = computed(() => {
    const view = this.view();
    return view ? sequenceOf(view) : null;
  });

  private readonly loads = new Subject<string>();

  constructor() {
    // switchMap: a second id arriving while the first read is out abandons that read.
    this.loads
      .pipe(
        switchMap((id) =>
          defer(() => {
            this.loading.set(true);
            this.error.set(null);
            if (this.view()?.correlationId !== id) {
              this.view.set(null);
              this.diff.set(null);
            }
            return this.host.trace(id, this.shownWindow());
          }).pipe(
            catchError((e: unknown) => {
              this.error.set(this.describeError(e));
              return EMPTY;
            }),
            finalize(() => this.loading.set(false)),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((view) => {
        // A Grafana that did not answer arrives as a successful response carrying reachable:false; it is an error
        // over whatever diagram is on screen, as the Trace widget shows it, and not a diagram of nothing.
        if (!view.reachable) {
          this.error.set(view.error ?? 'The trace could not be built.');
          return;
        }
        const current = { fetchedAt: new Date(), view };
        const previous = this.history.record(view, current.fetchedAt);
        this.diff.set(previous ? diffTimelines(previous, current) : null);
        this.view.set(view);
      });

    // Where there is no id to start from, nothing is read: the config arrives after the constructor, so the first
    // draw waits for the effect. A configured id changed later (the settings editor, a preset) is drawn when it appears.
    let seen: string | undefined;
    effect(() => {
      const configured = this.configuredId();
      const first = seen === undefined;
      const changed = !first && configured !== seen;
      seen = configured;
      if (first) {
        const id = untracked(() => this.drawn()) || configured;
        if (id) untracked(() => this.draw(id));
      } else if (changed && configured) {
        untracked(() => this.draw(configured));
      }
    });

    // A window the config comes to name later outranks the one the operator chose before it (as the Trace widget does).
    let seenWindow: string | undefined;
    effect(() => {
      const configured = this.configuredWindow();
      const changed = seenWindow !== undefined && configured !== seenWindow;
      seenWindow = configured;
      if (changed) untracked(() => this.window.set(''));
    });

    // A link pointed at the followers after this widget appeared; one made before is not replayed into it.
    let handled = this.follow.pointed()?.n ?? 0;
    effect((onCleanup) => {
      if (!this.following()) return;
      onCleanup(this.follow.follow(this.destroyRef));
    });
    effect(() => {
      const pointed = this.follow.pointed();
      if (pointed && pointed.n > handled) {
        handled = pointed.n;
        if (untracked(() => this.following())) untracked(() => this.draw(pointed.id));
      }
    });
  }

  /** Puts `id` in the box and reads its timeline. */
  private draw(id: string): void {
    this.correlationId.set(id);
    this.drawn.set(id);
    this.loads.next(id);
  }

  submit(): void {
    const id = this.correlationId().trim();
    if (id !== '') this.draw(id);
  }

  /** Reads the id already on screen again, for when the window changes or the request has moved on. */
  reload(): void {
    const id = this.view()?.correlationId ?? this.correlationId().trim();
    if (id !== '') this.loads.next(id);
  }

  canReload(): boolean {
    return this.view() !== null || this.correlationId().trim() !== '';
  }

  private describeError(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
