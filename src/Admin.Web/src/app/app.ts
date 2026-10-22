import { CdkDrag, CdkDragDrop, CdkDropList } from '@angular/cdk/drag-drop';
import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatToolbarModule } from '@angular/material/toolbar';
import { catchError, of } from 'rxjs';
import { HostClient } from './core/host/host-client';
import { THEME_CHOICES, ThemeService } from './core/theme/theme-service';
import { CommandPalette } from './shared/command-palette/command-palette';
import { differsFromBuiltIn } from './workspace/built-in-pages';
import { WIDGETS, WidgetPreset, WidgetType } from './workspace/widgets';
import { WorkspaceService } from './workspace/workspace-service';

/** The icon (a Material Symbols ligature) each theme choice shows on the top bar's switch. */
const THEME_ICONS = { system: 'brightness_auto', light: 'light_mode', dark: 'dark_mode' } as const;

/**
 * The shell (widget-workspace spec §7.2): a side navigation of the operator's pages in their order, and a top bar
 * with the open page's title, Edit layout, Add widget, the page's actions, where saving stands, the theme switch, the
 * backend directory and the palette key. The workspace is read once, here, for the whole session.
 */
@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    CdkDropList,
    CdkDrag,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatListModule,
    MatMenuModule,
    MatSidenavModule,
    MatToolbarModule,
    CommandPalette,
  ],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly host = inject(HostClient);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  readonly theme = inject(ThemeService);
  readonly workspace = inject(WorkspaceService);
  readonly widgetTypes = WIDGETS;
  readonly differsFromBuiltIn = differsFromBuiltIn;
  readonly themeChoices = THEME_CHOICES.map((value) => ({ value, icon: THEME_ICONS[value] }));
  /** On error, fall back to undefined so the shell (nav, router outlet) still renders without the backend-dir hint. */
  readonly config = toSignal(this.host.config().pipe(catchError(() => of(undefined))));

  constructor() {
    this.workspace.load();
  }

  newPage(): void {
    const id = this.workspace.addPage();
    this.workspace.editing.set(true);
    void this.router.navigate(this.workspace.pageLink(id));
  }

  rename(id: string, title: string): void {
    this.workspace.renamePage(id, title);
  }

  addWidget(pageId: string, kind: WidgetType, preset: WidgetPreset): void {
    this.workspace.editing.set(true);
    this.workspace.addWidget(pageId, {
      type: kind.type,
      cols: kind.defaultSize.cols,
      rows: kind.defaultSize.rows,
      config: structuredClone(preset.config),
    });
  }

  duplicate(id: string): void {
    const copy = this.workspace.duplicatePage(id);
    if (copy) void this.router.navigate(this.workspace.pageLink(copy));
  }

  /** An operator's page is deleted with an undo; a built-in page hides. Either way the open page moves on. */
  remove(id: string): void {
    const pages = this.workspace.pages();
    const at = pages.findIndex((p) => p.id === id);
    const page = pages[at];
    if (!page) return;
    this.workspace.deletePage(id);
    const next = this.workspace.visiblePages()[0];
    if (next) void this.router.navigate(this.workspace.pageLink(next.id));
    if (page.builtIn) return;
    this.snackBar
      .open(`Deleted "${page.title}"`, 'Undo', { duration: 8000 })
      .onAction()
      .subscribe(() => {
        this.workspace.restorePage(page, at);
        void this.router.navigate(this.workspace.pageLink(page.id));
      });
  }

  dropped(event: CdkDragDrop<unknown>): void {
    const visible = this.workspace.visiblePages();
    const moved = visible[event.previousIndex];
    const target = visible[event.currentIndex];
    if (moved && target) this.workspace.movePage(moved.id, target.id);
  }
}
