import { NgComponentOutlet, NgTemplateOutlet } from '@angular/common';
import { Component, ElementRef, Injector, Type, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { Gridster, GridsterConfig, GridsterItem, GridsterItemConfig } from 'angular-gridster2';
import { WIDGET_CONTEXT, WidgetContext } from './widget-context';
import { fieldText, fieldValue } from './widget-config';
import { WidgetField, WidgetPreset, WidgetType, widgetType } from './widgets';
import { GRID_COLUMNS, MAX_ROWS, PAGE_ROWS, WidgetInstance } from './workspace-types';

/** One grid row, in pixels; the spec leaves the height to the builder (§12). */
export const ROW_HEIGHT_PX = 40;

/** A widget as the grid holds it: gridster moves `x`, `y`, `cols` and `rows` on this object in place. */
interface GridItem extends GridsterItemConfig {
  widget: WidgetInstance;
  kind: WidgetType | undefined;
  config: Record<string, unknown>;
}

/**
 * The page's grid (widget-workspace spec §7.3): every widget in a cell grid of GRID_COLUMNS columns, locked unless
 * `editing`. The one place `angular-gridster2` is named, so the library can be swapped for Gridstack.js behind it.
 * A move or a resize is reported once, when it ends, as the page's whole layout. A widget's header carries its
 * configure control (editing only; it offers the type's presets, spec §7.2) and maximise, which shows that one widget
 * across the whole page until it is restored; the others are not rendered meanwhile, and keep what they held in the
 * session store (§4.2).
 */
@Component({
  selector: 'app-workspace-grid',
  imports: [Gridster, GridsterItem, NgComponentOutlet, NgTemplateOutlet, MatButtonModule, MatIconModule, MatMenuModule],
  templateUrl: './workspace-grid.html',
  styleUrl: './workspace-grid.css',
})
export class WorkspaceGrid {
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly pageId = input.required<string>();
  readonly widgets = input.required<WidgetInstance[]>();
  readonly editing = input(false);

  /**
   * Each widget's injector, which tells it its instance and page (WIDGET_CONTEXT). Kept per instance, because a new
   * injector makes `*ngComponentOutlet` create the widget again, and a drag recomputes every item.
   */
  private readonly injectors = new Map<string, Injector>();

  injectorOf(item: GridItem): Injector {
    const context: WidgetContext = { instanceId: item.widget.id, pageId: this.pageId() };
    const key = `${context.pageId}\n${context.instanceId}`;
    let injector = this.injectors.get(key);
    if (!injector) {
      injector = Injector.create({ providers: [{ provide: WIDGET_CONTEXT, useValue: context }], parent: this.injector });
      this.injectors.set(key, injector);
    }
    return injector;
  }

  readonly layoutChange = output<WidgetInstance[]>();
  readonly removeWidget = output<string>();
  readonly dragging = output<boolean>();
  readonly configureWidget = output<{ id: string; config: Record<string, unknown> }>();

  private readonly maximisedId = signal<string | null>(null);

  /** The loaded component of each type, so `*ngComponentOutlet` has a class to render. */
  private readonly components = signal<ReadonlyMap<string, Type<unknown>>>(new Map());

  constructor() {
    effect(() => {
      for (const item of this.items()) if (item.kind) this.loadComponent(item.kind);
    });
    // Leaving the layout editor closes an open settings panel, so the next edit starts without a stale draft.
    effect(() => {
      if (!this.editing()) untracked(() => this.settings.set(null));
    });
    // A page switch or a removed widget leaves no draft behind to reappear under another widget or page.
    effect(() => {
      this.pageId();
      untracked(() => this.settings.set(null));
    });
    effect(() => {
      const items = this.items();
      const open = untracked(this.settings);
      if (open && !items.some((i) => i.widget.id === open.id)) untracked(() => this.settings.set(null));
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

  /** The widget shown across the page, or null; one that is removed while maximised simply ends it. */
  readonly maximised = computed(() => this.items().find((i) => i.widget.id === this.maximisedId()) ?? null);

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
    return item.kind ? item.kind.titleOf(item.config) : $localize`Unknown widget (${item.widget.type}:type:)`;
  }

  removeLabel(item: GridItem): string {
    return $localize`Remove ${this.titleOf(item)}:title:`;
  }

  isMaximised(item: GridItem): boolean {
    return this.maximisedId() === item.widget.id;
  }

  toggleMaximise(item: GridItem): void {
    this.maximisedId.set(this.isMaximised(item) ? null : item.widget.id);
  }

  /**
   * Only a type with a choice to make has anything to configure: one preset is the config it already has. A type
   * whose presets are starting points (a note) is not offered them again, since a choice would replace its text.
   */
  canConfigure(item: GridItem): boolean {
    return item.kind?.configurable !== false && ((item.kind?.presets.length ?? 0) > 1 || this.hasFields(item));
  }

  /** Whether the type has settings a preset cannot hold, which the menu's "Edit settings" opens. */
  hasFields(item: GridItem): boolean {
    return (item.kind?.fields?.length ?? 0) > 0;
  }

  /** Whether a field is a list of names, which is the one kind the panel's comma hint is about. */
  hasNames(item: GridItem): boolean {
    return item.kind?.fields?.some((f) => f.kind === 'names') ?? false;
  }

  /** The widget whose settings are open, with the text of each field as it is being written. */
  readonly settings = signal<{ id: string; draft: Record<string, string> } | null>(null);

  openSettings(item: GridItem): void {
    const draft: Record<string, string> = {};
    for (const field of item.kind?.fields ?? []) draft[field.key] = fieldText(field.kind, item.config[field.key]);
    this.settings.set({ id: item.widget.id, draft });
    this.focusWanted = true;
  }

  /** The menu has closed and handed focus back to its button: a panel just opened takes it for its first field. */
  focusSettings(): void {
    if (!this.focusWanted) return;
    this.focusWanted = false;
    setTimeout(() => this.host.nativeElement.querySelector<HTMLInputElement>('.ws-settings input')?.focus());
  }

  private focusWanted = false;

  isEditingSettings(item: GridItem): boolean {
    return this.settings()?.id === item.widget.id;
  }

  draftOf(field: WidgetField): string {
    return this.settings()?.draft[field.key] ?? '';
  }

  setDraft(field: WidgetField, text: string): void {
    this.settings.update((s) => (s ? { ...s, draft: { ...s.draft, [field.key]: text } } : s));
  }

  /** A preset replaces the config, so a draft written against the old one is dropped rather than applied over it. */
  choosePreset(item: GridItem, preset: WidgetPreset): void {
    this.closeSettings();
    this.configureWidget.emit({ id: item.widget.id, config: preset.config });
  }

  /** Closes the panel; given the widget it was for, focus goes back to that widget's configure button. */
  closeSettings(item?: GridItem): void {
    this.settings.set(null);
    if (!item) return;
    setTimeout(() => {
      const frame = [...this.host.nativeElement.querySelectorAll('[data-widget-id]')].find(
        (e) => e.getAttribute('data-widget-id') === item.widget.id,
      );
      frame?.querySelector<HTMLElement>('button.ws-configure')?.focus();
    });
  }

  /** Saves the written fields over the config the widget holds, through the type's own `config`, then closes. */
  applySettings(item: GridItem): void {
    const open = this.settings();
    const kind = item.kind;
    if (!open || !kind || open.id !== item.widget.id) return;
    const config: Record<string, unknown> = { ...item.config };
    for (const field of kind.fields ?? []) config[field.key] = fieldValue(field.kind, open.draft[field.key] ?? '');
    this.configureWidget.emit({ id: item.widget.id, config: kind.config(config) });
    this.closeSettings(item);
  }

  settingsLabel(item: GridItem): string {
    return $localize`Settings of ${this.titleOf(item)}:title:`;
  }

  /** Whether `preset` is the choice `item` holds now, by the config its type accepted. */
  isCurrent(item: GridItem, preset: WidgetPreset): boolean {
    const kind = item.kind;
    return !!kind && JSON.stringify(kind.config(preset.config)) === JSON.stringify(item.config);
  }

  configureLabel(item: GridItem): string {
    return $localize`Configure ${this.titleOf(item)}:title:`;
  }

  maximiseLabel(item: GridItem): string {
    return this.isMaximised(item)
      ? $localize`Restore ${this.titleOf(item)}:title: to the page`
      : $localize`Maximise ${this.titleOf(item)}:title:`;
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
