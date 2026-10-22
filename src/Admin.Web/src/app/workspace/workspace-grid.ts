import { NgComponentOutlet } from '@angular/common';
import { Component, Type, computed, effect, input, output, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Gridster, GridsterConfig, GridsterItem, GridsterItemConfig } from 'angular-gridster2';
import { WidgetType, widgetType } from './widgets';
import { GRID_COLUMNS, MAX_ROWS, WidgetInstance } from './workspace-types';

/** One grid row, in pixels; the spec leaves the height to the builder (§12). */
export const ROW_HEIGHT_PX = 40;

/**
 * How many rows a page's grid has. gridster's own default is 100, and a widget it cannot place there is hidden
 * without a word, while "Add widget" puts each new one below the last: the seventh full-size screen would be saved
 * and never shown. The host bounds a widget's height and not where it starts, so this is the SPA's limit.
 */
export const PAGE_ROWS = 2000;

/** A widget as the grid holds it: gridster moves `x`, `y`, `cols` and `rows` on this object in place. */
interface GridItem extends GridsterItemConfig {
  widget: WidgetInstance;
  kind: WidgetType | undefined;
  config: Record<string, unknown>;
}

/**
 * The page's grid (widget-workspace spec §7.3): every widget in a cell grid of GRID_COLUMNS columns, locked unless
 * `editing`. The one place `angular-gridster2` is named, so the library can be swapped for Gridstack.js behind it.
 * A move or a resize is reported once, when it ends, as the page's whole layout.
 */
@Component({
  selector: 'app-workspace-grid',
  imports: [Gridster, GridsterItem, NgComponentOutlet, MatButtonModule, MatIconModule],
  templateUrl: './workspace-grid.html',
  styleUrl: './workspace-grid.css',
})
export class WorkspaceGrid {
  readonly widgets = input.required<WidgetInstance[]>();
  readonly editing = input(false);

  readonly layoutChange = output<WidgetInstance[]>();
  readonly removeWidget = output<string>();
  readonly dragging = output<boolean>();

  /** The loaded component of each type, so `*ngComponentOutlet` has a class to render. */
  private readonly components = signal<ReadonlyMap<string, Type<unknown>>>(new Map());

  constructor() {
    effect(() => {
      for (const item of this.items()) if (item.kind) this.loadComponent(item.kind);
    });
  }

  readonly items = computed<GridItem[]>(() =>
    this.widgets().map((widget) => {
      const kind = widgetType(widget.type);
      return {
        x: widget.x,
        y: widget.y,
        cols: widget.cols,
        rows: widget.rows,
        minItemCols: kind?.minSize.cols ?? 1,
        minItemRows: kind?.minSize.rows ?? 1,
        widget,
        kind,
        config: kind ? kind.config(widget.config) : widget.config,
      };
    }),
  );

  readonly options = computed<GridsterConfig>(() => {
    const editing = this.editing();
    return {
      gridType: 'verticalFixed',
      fixedRowHeight: ROW_HEIGHT_PX,
      minCols: GRID_COLUMNS,
      maxCols: GRID_COLUMNS,
      maxRows: PAGE_ROWS,
      maxItemRows: MAX_ROWS,
      margin: 8,
      outerMargin: true,
      compactType: 'none',
      pushItems: true,
      displayGrid: editing ? 'onDrag&Resize' : 'none',
      disableWarnings: true,
      // Only the header's handle drags, so a click into a screen's form never moves its widget.
      draggable: {
        enabled: editing,
        dragHandleClass: 'widget-drag',
        ignoreContent: true,
        start: () => this.dragging.emit(true),
        stop: () => this.dragging.emit(false),
      },
      resizable: {
        enabled: editing,
        start: () => this.dragging.emit(true),
        stop: () => this.dragging.emit(false),
      },
      itemChangeCallback: () => this.reportLayout(),
    };
  });

  componentOf(item: GridItem): Type<unknown> | null {
    return item.kind ? (this.components().get(item.kind.type) ?? null) : null;
  }

  titleOf(item: GridItem): string {
    return item.kind ? item.kind.titleOf(item.config) : `Unknown widget (${item.widget.type})`;
  }

  private reportLayout(): void {
    // Read back what gridster moved: the item objects are its, the widget instances are the page's.
    this.layoutChange.emit(
      this.items().map((item) => ({ ...item.widget, x: item.x, y: item.y, cols: item.cols, rows: item.rows })),
    );
  }

  private loadComponent(kind: WidgetType): void {
    if (untracked(this.components).has(kind.type)) return;
    void kind.component().then((component) =>
      this.components.update((loaded) => new Map(loaded).set(kind.type, component)),
    );
  }
}
