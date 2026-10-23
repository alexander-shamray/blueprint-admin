import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, map } from 'rxjs';
import { HostClient } from '../core/host/host-client';
import { isScreenPath } from '../screens';
import { builtInTitle, defaultWorkspace, retireLegacy, widgetsOf, withEveryBuiltIn } from './built-in-pages';
import { WidgetSessionStore } from './widget-session-store';
import { PAGE_ROWS, Page, WidgetInstance, Workspace, newId } from './workspace-types';

/** How long after the last change a save waits (widget-workspace spec §6.2: "about 1 s"). */
export const SAVE_DELAY_MS = 1000;

/**
 * Where saving stands, for the top bar. `unavailable` is a workspace that could not be read, so there is no etag to
 * save over and nothing is sent; `conflict` is a 412, holding what the other tab saved.
 */
export type SaveState =
  | { kind: 'saved' }
  | { kind: 'pending' }
  | { kind: 'saving' }
  | { kind: 'failed'; message: string }
  | { kind: 'conflict'; theirs: Workspace; etag: string }
  | { kind: 'unavailable'; message: string };

/**
 * The operator's pages, held for the whole session and saved to the host (widget-workspace spec §4.1, §6). Every
 * change goes through `change`, which saves about a second after the last one and never while a widget is being
 * dragged or resized, so a drag is one save rather than one per frame.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceService {
  private readonly host = inject(HostClient);
  private readonly sessions = inject(WidgetSessionStore);

  readonly workspace = signal<Workspace>(defaultWorkspace());
  readonly loaded = signal(false);
  readonly saveState = signal<SaveState>({ kind: 'saved' });
  /** Edit layout: the grid is locked unless this is on (§7.2). */
  readonly editing = signal(false);
  /** The page the router has open; the shell's title and page actions follow it. */
  readonly currentPageId = signal<string | null>(null);
  /**
   * The titles of the pages whose legacy screen widgets the last read replaced (§5.3, phase 6), which the shell says
   * until it is dismissed. The replacement is saved at once, so it is said once.
   */
  readonly retired = signal<string[]>([]);

  readonly pages = computed(() => this.workspace().pages);
  readonly visiblePages = computed(() => this.pages().filter((p) => !p.hidden));
  readonly hiddenPages = computed(() => this.pages().filter((p) => p.hidden));
  readonly currentPage = computed(() => this.pages().find((p) => p.id === this.currentPageId()) ?? null);

  private etag: string | null = null;
  private dragging = false;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private inFlight = false;
  /** Bumped by every change, so a save that returns can tell whether it carried the latest. */
  private revision = 0;
  /** Bumped by an import, whose etag a save that was already on its way must not overwrite. */
  private epoch = 0;

  load(): void {
    this.host.workspace().subscribe({
      next: ({ workspace, etag }) => {
        const migrated = this.adopt(workspace);
        this.etag = etag;
        this.loaded.set(true);
        if (migrated) this.saveMigration();
      },
      error: (e: unknown) => {
        this.loaded.set(true);
        this.saveState.set({ kind: 'unavailable', message: describe(e) });
      },
    });
  }

  /**
   * The route a page opens at: a built-in page keeps its screen's old path. One built in by a newer console names a
   * screen no route here knows, so it opens by id like any other, and survives this console (§6.3).
   */
  pageLink(id: string): string[] {
    const page = this.pages().find((p) => p.id === id);
    return page?.builtIn && isScreenPath(page.builtIn) ? ['/', page.builtIn] : ['/page', id];
  }

  open(id: string | null): void {
    this.currentPageId.set(id);
    // The open page is part of the document, but opening one is not an edit: it rides along with the next save.
    if (id !== null && this.workspace().activePageId !== id) {
      this.workspace.update((w) => ({ ...w, activePageId: id }));
    }
  }

  addPage(): string {
    const id = newId('page');
    this.change((w) => ({
      ...w,
      pages: [...w.pages, { id, title: 'New page', builtIn: null, hidden: false, widgets: [] }],
    }));
    return id;
  }

  renamePage(id: string, title: string): void {
    const trimmed = title.trim();
    if (trimmed === '') return;
    this.changePage(id, (p) => ({ ...p, title: trimmed }));
  }

  /** Puts `id` where `target` stands, among all the pages, hidden ones included. */
  movePage(id: string, target: string): void {
    if (id === target) return;
    this.change((w) => {
      const pages = [...w.pages];
      const from = pages.findIndex((p) => p.id === id);
      const to = pages.findIndex((p) => p.id === target);
      if (from < 0 || to < 0) return w;
      const [page] = pages.splice(from, 1);
      pages.splice(to, 0, page);
      return { ...w, pages };
    });
  }

  /** One place up or down among the visible pages. */
  movePageBy(id: string, step: -1 | 1): void {
    const visible = this.visiblePages();
    const at = visible.findIndex((p) => p.id === id);
    const target = visible[at + step];
    if (at >= 0 && target) this.movePage(id, target.id);
  }

  setHidden(id: string, hidden: boolean): void {
    this.changePage(id, (p) => ({ ...p, hidden }));
  }

  /** Removes an operator's page; a built-in page hides instead, so its default is always one click away (§5.3). */
  deletePage(id: string): void {
    const page = this.pages().find((p) => p.id === id);
    if (!page) return;
    if (page.builtIn) {
      this.setHidden(id, true);
      return;
    }
    this.change((w) => ({ ...w, pages: w.pages.filter((p) => p.id !== id) }));
  }

  /** A deleted page put back where it stood: the delete's undo. */
  restorePage(page: Page, index: number): void {
    if (this.pages().some((p) => p.id === page.id)) return;
    this.change((w) => {
      const pages = [...w.pages];
      pages.splice(Math.min(index, pages.length), 0, page);
      return { ...w, pages };
    });
  }

  /** A copy of a page as the operator's own: never built-in, and every widget a new id, which the host requires. */
  duplicatePage(id: string): string | null {
    const page = this.pages().find((p) => p.id === id);
    if (!page) return null;
    const copy = {
      id: newId('page'),
      title: `${page.title} (copy)`,
      builtIn: null,
      hidden: false,
      widgets: widgetsOf(page).map((w) => ({ ...w, id: newId('widget'), config: structuredClone(w.config) })),
    };
    this.change((w) => {
      const at = w.pages.findIndex((p) => p.id === id);
      const pages = [...w.pages];
      pages.splice(at + 1, 0, copy);
      return { ...w, pages };
    });
    return copy.id;
  }

  /** A built-in page back as shipped: its default widgets and title, and saved again as a reference (§5.3). */
  resetPage(id: string): void {
    const page = this.pages().find((p) => p.id === id);
    const screen = page?.builtIn;
    if (!page || !screen) return;
    // The defaults reuse their `builtin-…` ids, so what those widgets kept goes too: a reset starts them afresh.
    this.sessions.forget(widgetsOf(page).map((w) => w.id));
    this.changePage(id, (p) => ({ ...p, title: builtInTitle(screen), widgets: null }));
  }

  /** The page's widgets after a drag, a resize, an add or a remove; a built-in page is saved whole from here on. */
  setWidgets(id: string, widgets: WidgetInstance[]): void {
    this.changePage(id, (p) => ({ ...p, widgets }));
  }

  /**
   * Puts a widget below everything already on the page: a fixed place nothing has to be pushed out of. False when it
   * would end below PAGE_ROWS, where the grid could not show it; nothing is added then.
   */
  addWidget(id: string, widget: Omit<WidgetInstance, 'id' | 'x' | 'y'>): boolean {
    const page = this.pages().find((p) => p.id === id);
    if (!page) return false;
    const current = widgetsOf(page);
    const y = current.reduce((bottom, w) => Math.max(bottom, w.y + w.rows), 0);
    if (y + widget.rows > PAGE_ROWS) return false;
    this.setWidgets(id, [...current, { ...widget, id: newId('widget'), x: 0, y }]);
    return true;
  }

  /** A widget given another config, one a type's presets offer; the rest of the page is as it was. */
  configureWidget(id: string, widgetId: string, config: Record<string, unknown>): void {
    const page = this.pages().find((p) => p.id === id);
    if (!page) return;
    this.setWidgets(
      id,
      widgetsOf(page).map((w) => (w.id === widgetId ? { ...w, config: structuredClone(config) } : w)),
    );
  }

  /**
   * Replaces the whole workspace with an exported document (§6.2), which the host validates as a save is. What the
   * old widgets kept for the session goes with them, and a save already on its way no longer counts. An error is the
   * host's refusal, which `describe` reads; the workspace is then as it was.
   */
  importWorkspace(document: unknown): Observable<void> {
    return this.host.importWorkspace(document).pipe(
      map(({ workspace, etag }) => {
        if (this.timer !== null) clearTimeout(this.timer);
        this.timer = null;
        this.epoch++;
        this.revision++;
        this.sessions.forget(this.pages().flatMap((p) => widgetsOf(p).map((w) => w.id)));
        const migrated = this.adopt(workspace);
        this.etag = etag;
        this.saveState.set({ kind: 'saved' });
        if (migrated) this.saveMigration();
      }),
    );
  }

  /** A widget taken off its page, and what it kept for the session with it: a remove has no undo. */
  removeWidget(id: string, widgetId: string): void {
    const page = this.pages().find((p) => p.id === id);
    if (!page) return;
    this.setWidgets(
      id,
      widgetsOf(page).filter((w) => w.id !== widgetId),
    );
    this.sessions.forget([widgetId]);
  }

  /** A drag or a resize starting or ending: nothing is saved in between, and the end saves what it left. */
  setDragging(dragging: boolean): void {
    this.dragging = dragging;
    if (!dragging && this.saveState().kind === 'pending') this.schedule();
  }

  retry(): void {
    this.schedule(0);
  }

  /** A 412 settled: take what the other tab saved, or save this tab's over it. */
  resolveConflict(take: 'theirs' | 'mine'): void {
    const state = this.saveState();
    if (state.kind !== 'conflict') return;
    this.etag = state.etag;
    // Settled before scheduling: `schedule` refuses to save over an unsettled conflict.
    this.saveState.set({ kind: 'saved' });
    if (take === 'mine') this.schedule(0);
    else if (this.adopt(state.theirs)) this.saveMigration();
  }

  /** The retired-widget notice, read. */
  dismissRetired(): void {
    this.retired.set([]);
  }

  /**
   * A document from the host made this tab's workspace: its legacy screen widgets replaced, then every built-in page
   * in place. True when the replacement changed a page, which the caller then saves.
   */
  private adopt(workspace: Workspace): boolean {
    const { workspace: current, pages } = retireLegacy(workspace);
    this.workspace.set(withEveryBuiltIn(current));
    if (pages.length > 0) this.retired.set(pages);
    return pages.length > 0;
  }

  /** The replacement saved like any change, so the notice is not said again on the next read. */
  private saveMigration(): void {
    this.revision++;
    this.schedule();
  }

  private changePage(id: string, edit: (page: Page) => Page): void {
    this.change((w) => ({ ...w, pages: w.pages.map((p) => (p.id === id ? edit(p) : p)) }));
  }

  private change(edit: (workspace: Workspace) => Workspace): void {
    this.workspace.update(edit);
    this.revision++;
    this.schedule();
  }

  private schedule(delay = SAVE_DELAY_MS): void {
    const kind = this.saveState().kind;
    // Nothing to save over, or a conflict the operator has not settled: a save now would only repeat the refusal.
    if (this.etag === null || kind === 'unavailable' || kind === 'conflict') return;
    if (this.timer !== null) clearTimeout(this.timer);
    this.saveState.set({ kind: 'pending' });
    this.timer = setTimeout(() => {
      this.timer = null;
      this.save();
    }, delay);
  }

  /** A save from before an import came back and is dropped; an edit made since waited behind it, so it goes now. */
  private resumeAfterImport(): void {
    if (this.saveState().kind === 'pending') this.schedule(0);
  }

  private save(): void {
    if (this.dragging || this.inFlight || this.etag === null) return;
    const sent = this.revision;
    const epoch = this.epoch;
    this.inFlight = true;
    this.saveState.set({ kind: 'saving' });
    this.host.saveWorkspace(this.workspace(), this.etag).subscribe({
      next: (answer) => {
        this.inFlight = false;
        if (epoch !== this.epoch) return this.resumeAfterImport();
        if (answer.kind === 'stale') {
          this.saveState.set({ kind: 'conflict', theirs: answer.workspace, etag: answer.etag });
          return;
        }
        this.etag = answer.etag;
        this.saveState.set({ kind: 'saved' });
        if (this.revision !== sent) this.schedule();
      },
      error: (e: unknown) => {
        this.inFlight = false;
        if (epoch !== this.epoch) return this.resumeAfterImport();
        this.saveState.set({ kind: 'failed', message: describe(e) });
      },
    });
  }
}

/** What a failed host call says, for a notice: the problem's `detail`, else the status. */
export function describe(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    const detail = (e.error as { detail?: unknown } | null)?.detail;
    return typeof detail === 'string' ? detail : e.status === 0 ? 'the host did not answer' : `HTTP ${e.status}`;
  }
  return String(e);
}
