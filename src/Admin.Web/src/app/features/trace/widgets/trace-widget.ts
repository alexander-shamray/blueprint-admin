import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { EMPTY, Subject, catchError, defer, finalize, switchMap } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { TraceEvent, TraceEventKind, TraceView } from '../../../core/host/host-types';
import { widgetSession } from '../../../workspace/widget-session-store';
import { TraceDiff, TraceHistory, diffTimelines, eventKey } from '../trace-history';
import { TraceFollow } from '../trace-follow';

/** The windows the select offers. The host accepts anything from 1m to 24h; these are the useful ones. */
export const WINDOWS = ['15m', '1h', '6h', '24h'] as const;

/** Each kind's word, because state is never carried by colour alone. */
const KIND_WORDS: Record<TraceEventKind, string> = {
  HttpIn: '[http]',
  Log: '[log]',
  Span: '[span]',
  Outbox: '[outbox]',
  Publish: '[publish]',
  Consume: '[consume]',
  Saga: '[saga]',
  Queued: '[queued]',
  Error: '[error]',
};

/**
 * One correlation id's timeline (spec §5.9, §6; widget-workspace spec §5.2). Which id: the one in the route of the
 * built-in Trace page (`/trace/:correlationId`), which drives the load so the URL is shareable and the back button
 * works; else the one a link last pointed it at, if the widget follows; else the one it was typed or configured with.
 * Config: `correlationId` and `window` to start with, and `follow`, which makes a correlation-id link anywhere on the
 * page re-point this widget instead of opening the Trace page (`TraceFollow`). The id last traced is session state.
 */
@Component({
  selector: 'app-trace-widget',
  imports: [DatePipe, FormsModule],
  templateUrl: './trace-widget.html',
  styleUrl: './trace-widget.css',
  // Keep the whitespace between adjacent <td>s so row text reads as separate cells, as the Broker screen does.
  preserveWhitespaces: true,
})
export class TraceWidget {
  private readonly host = inject(HostClient);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute, { optional: true });
  private readonly destroyRef = inject(DestroyRef);
  private readonly history = inject(TraceHistory);
  private readonly follow = inject(TraceFollow);

  readonly config = input<Record<string, unknown>>({});

  readonly windows = WINDOWS;
  readonly view = signal<TraceView | null>(null);
  /** What moved since this session's previous fetch of the id on screen; null on its first fetch. */
  readonly diff = signal<TraceDiff | null>(null);
  private arrivedKeys = new Set<string>();
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly correlationId = signal('');
  /** The window the operator chose, or '' while they have not: session state (§4.2), over the configured one. */
  readonly window = widgetSession('window', '');
  readonly shownWindow = computed(() => this.window() || this.configuredWindow());

  /** The id last traced, kept for the tab so a re-created widget reads it again. */
  private readonly traced = widgetSession('traced', '');

  private readonly configuredId = computed(() => {
    const id = this.config()['correlationId'];
    return typeof id === 'string' ? id.trim() : '';
  });
  private readonly configuredWindow = computed(() => {
    const w = this.config()['window'];
    return WINDOWS.find((x) => x === w) ?? WINDOWS[0];
  });
  readonly following = computed(() => this.config()['follow'] === true);

  /** Every load request, from the route, a link, Trace or Reload; the latest wins. */
  private readonly loads = new Subject<string>();

  /**
   * The id in the URL drives the load, and it is watched rather than read once: navigating from one
   * id to another reuses this component, so a constructor that only read the first param would leave
   * the old timeline on screen.
   */
  constructor() {
    // switchMap, not a re-entrancy guard: a second id arriving while the first read is out must
    // abandon that read and load the new one. Dropping it would leave the URL naming an id whose
    // timeline is never fetched.
    this.loads
      .pipe(
        switchMap((id) =>
          defer(() => {
            this.loading.set(true);

            // Cleared at the start of every load, for the same reason the view is: an error left
            // over from the previous id would otherwise sit under the new URL while its read is out.
            this.error.set(null);

            // Cleared before the read, not after it fails: the template renders the view above the
            // loading branch, so keeping it would show A's timeline for the whole of B's request
            // while the URL and the input both say B. A reload of the id already on screen keeps its
            // last good timeline, as the Broker screen does.
            if (this.view()?.correlationId !== id) {
              this.view.set(null);
              this.setDiff(null);
            }

            return this.host.trace(id, this.shownWindow());
          }).pipe(
            catchError((e: unknown) => {
              this.error.set(this.describeError(e));
              return EMPTY;
            }),
            // Runs on cancellation too, before the next read's defer sets it again.
            finalize(() => this.loading.set(false)),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((view) => {
        // A Grafana that did not answer arrives as a successful response carrying reachable:false.
        // It is shown the same way a thrown HTTP failure is — through `error`, over whatever timeline
        // is already on screen — because from the operator's seat they are the same event, and
        // clearing the timeline for one while keeping it for the other would be incoherent. The view
        // was already cleared at load start if this is a different id, so nothing of another id's
        // timeline can survive here.
        if (!view.reachable) {
          this.error.set(view.error ?? 'The trace could not be built.');
          return;
        }

        // Only a reachable answer is history: an outage is not a timeline that lost every event.
        const current = { fetchedAt: new Date(), view };
        const previous = this.history.record(view, current.fetchedAt);
        this.setDiff(previous ? diffTimelines(previous, current) : null);
        this.view.set(view);
      });

    const fromRoute = this.route?.snapshot.paramMap.get('correlationId') ?? '';
    // Where there is no route id, the widget starts at the id it last traced, else the one it is configured with.
    // Its config arrives after the constructor, so that first load waits for the effect below.
    // A configured id changed later (the settings editor, a preset) is traced when it appears.
    let seen: string | undefined;
    effect(() => {
      const configured = this.configuredId();
      const first = seen === undefined;
      const changed = !first && configured !== seen;
      seen = configured;
      if (fromRoute) return;
      if (first) {
        const id = untracked(() => this.traced()) || configured;
        if (id) untracked(() => this.trace(id));
      } else if (changed && configured) {
        untracked(() => this.trace(configured));
      }
    });

    // A window the config comes to name later (the settings editor, a preset) outranks the one the operator chose
    // before it, as a configured operation does in the API widget. The next read uses it; the id on screen is not
    // fetched again for a window alone.
    let seenWindow: string | undefined;
    effect(() => {
      const configured = this.configuredWindow();
      const changed = seenWindow !== undefined && configured !== seenWindow;
      seenWindow = configured;
      if (changed) untracked(() => this.window.set(''));
    });

    this.route?.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const id = params.get('correlationId') ?? '';
      if (id === '') return;
      this.trace(id);
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
        if (untracked(() => this.following())) untracked(() => this.go(pointed.id));
      }
    });
  }

  /** Puts `id` in the box and loads its timeline. */
  private trace(id: string): void {
    this.correlationId.set(id);
    this.traced.set(id);
    this.loads.next(id);
  }

  /**
   * On the built-in Trace page the route is the single source of what is on screen, so Trace navigates and the route
   * param loads; anywhere else there is no route to carry the id, and the widget loads it itself.
   */
  submit(): void {
    const id = this.correlationId().trim();
    if (id !== '') this.go(id);
  }

  /**
   * Shows `id`: on the built-in Trace page by navigating to it, so the address says what is on screen (a link that
   * re-points a follower there does the same), anywhere else by loading it. Navigating to the address already showing
   * does nothing, so the id on screen is loaded directly.
   */
  private go(id: string): void {
    const onBuiltInPage = this.route?.snapshot.data['builtIn'] === 'trace';
    if (onBuiltInPage && this.route?.snapshot.paramMap.get('correlationId') !== id) void this.router.navigate(['/trace', id]);
    else this.trace(id);
  }

  /** Whether there is anything to reload: a timeline on screen, or an id typed into the input. */
  canReload(): boolean {
    return this.view() !== null || this.correlationId().trim() !== '';
  }

  kindWord(kind: TraceEventKind): string {
    return KIND_WORDS[kind];
  }

  /** Whether a row arrived since the previous fetch, so the table can say so in words as well as colour. */
  isArrived(event: TraceEvent): boolean {
    return this.arrivedKeys.has(eventKey(event));
  }

  private setDiff(diff: TraceDiff | null): void {
    this.arrivedKeys = new Set((diff?.arrived ?? []).map(eventKey));
    this.diff.set(diff);
  }

  /** Reloads the id already on screen, for when the window changes or the platform has moved on. */
  reload(): void {
    const id = this.view()?.correlationId ?? this.correlationId().trim();
    if (id !== '') this.loads.next(id);
  }

  /** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
  private describeError(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
