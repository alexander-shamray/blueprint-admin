import { Component, OnDestroy, computed, effect, inject, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { widgetsOf } from './built-in-pages';
import { WorkspaceGrid } from './workspace-grid';
import { WorkspaceService } from './workspace-service';
import { WidgetInstance } from './workspace-types';

/**
 * One page of the workspace, at the route that opened it: a built-in page at its screen's old path (`/stack`,
 * `/trace/:correlationId`, `/mail?correlationId=`), which route data names, or an operator's page at
 * `/page/:pageId`. The widgets inside read the same route, so an old link still hands them its parameters.
 */
@Component({
  selector: 'app-workspace-page',
  imports: [WorkspaceGrid, RouterLink],
  template: `
    @if (page(); as p) {
      @if (widgets().length === 0) {
        <p class="empty">This page has no widgets yet: choose <strong>Add widget</strong>.</p>
      }
      <app-workspace-grid
        class="grid"
        [pageId]="p.id"
        [widgets]="widgets()"
        [editing]="workspace.editing()"
        (layoutChange)="workspace.setWidgets(p.id, $event)"
        (removeWidget)="remove(p.id, $event)"
        (configureWidget)="workspace.configureWidget(p.id, $event.id, $event.config)"
        (dragging)="workspace.setDragging($event)"
      />
    } @else if (workspace.loaded()) {
      <p class="empty">There is no page here. <a routerLink="/">Go to the first page</a>.</p>
    }
  `,
  styles: `
    :host { display: flex; flex-direction: column; height: 100%; }
    .grid { flex: 1; min-height: 0; }
    .empty { flex: none; margin: 0; color: var(--app-muted); padding: 1rem; }
  `,
})
export class WorkspacePage implements OnDestroy {
  private readonly route = inject(ActivatedRoute);
  readonly workspace = inject(WorkspaceService);

  private readonly builtIn = toSignal(this.route.data.pipe(map((d) => (d['builtIn'] as string | undefined) ?? null)), {
    initialValue: null,
  });
  private readonly pageId = toSignal(this.route.paramMap.pipe(map((p) => p.get('pageId'))), { initialValue: null });

  readonly page = computed(() => {
    const builtIn = this.builtIn();
    const id = this.pageId();
    return this.workspace.pages().find((p) => (builtIn ? p.builtIn === builtIn : p.id === id)) ?? null;
  });

  readonly widgets = computed<WidgetInstance[]>(() => {
    const page = this.page();
    return page ? widgetsOf(page) : [];
  });

  constructor() {
    // Tell the shell which page is open, for its title and page actions; it follows a page found once loading ends.
    effect(() => {
      const id = this.page()?.id ?? null;
      untracked(() => this.workspace.open(id));
    });
  }

  remove(pageId: string, widgetId: string): void {
    this.workspace.removeWidget(pageId, widgetId);
  }

  ngOnDestroy(): void {
    if (this.workspace.currentPageId() === this.page()?.id) this.workspace.open(null);
  }
}
