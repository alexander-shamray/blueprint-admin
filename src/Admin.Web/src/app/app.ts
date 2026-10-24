import { CdkDrag, CdkDragDrop, CdkDropList } from '@angular/cdk/drag-drop';
import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatDividerModule } from '@angular/material/divider';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatToolbarModule } from '@angular/material/toolbar';
import { catchError, of } from 'rxjs';
import { AssistantPanel } from './assistant/assistant-panel';
import { AssistantService } from './assistant/assistant-service';
import { HostClient, WORKSPACE_EXPORT_URL } from './core/host/host-client';
import { LanguageService } from './core/i18n/language-service';
import { THEME_CHOICES, ThemeService } from './core/theme/theme-service';
import { CommandPalette } from './shared/command-palette/command-palette';
import { differsFromBuiltIn } from './workspace/built-in-pages';
import { ASSISTANT, WIDGETS, WidgetPreset, WidgetType } from './workspace/widgets';
import { RunningWork } from './workspace/running-work';
import { WorkspaceService, describe } from './workspace/workspace-service';

/** The icon (a Material Symbols ligature) each theme choice shows on the top bar's switch. */
const THEME_ICONS = { system: 'brightness_auto', light: 'light_mode', dark: 'dark_mode' } as const;

/** What each theme choice is called to assistive technology, the switch being icons only. */
const THEME_LABELS = {
  system: $localize`system theme`,
  light: $localize`light theme`,
  dark: $localize`dark theme`,
} as const;

/**
 * The shell (widget-workspace spec §7.2): a side navigation of the operator's pages in their order, and a top bar
 * with the open page's title, Edit layout, Add widget, the page's actions, where saving stands, the backend directory,
 * the palette key, the assistant's toggle when it is on, the language picker and the theme switch. The workspace is read once, here, for the whole session.
 */
@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    DecimalPipe,
    CdkDropList,
    CdkDrag,
    MatButtonModule,
    MatButtonToggleModule,
    MatDividerModule,
    MatIconModule,
    MatListModule,
    MatMenuModule,
    MatSidenavModule,
    MatToolbarModule,
    CommandPalette,
    AssistantPanel,
  ],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly host = inject(HostClient);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  readonly theme = inject(ThemeService);
  readonly languages = inject(LanguageService);
  readonly language = this.languages.choices.find((l) => l.id === this.languages.current) ?? this.languages.choices[0];
  /** The picker's name holds the language it shows, so a voice-control user can say what they see. */
  readonly languageLabel = $localize`Language: ${this.language.name}:language:`;
  readonly workspace = inject(WorkspaceService);
  readonly assistant = inject(AssistantService);
  readonly runningWork = inject(RunningWork);
  /** The Add widget picker: with the assistant off, no Assistant type (AI assistant design §9.1); a saved one still renders. */
  readonly widgetTypes = computed(() => WIDGETS.filter((w) => w.type !== ASSISTANT.type || this.assistant.enabled()));
  readonly differsFromBuiltIn = differsFromBuiltIn;
  readonly themeChoices = THEME_CHOICES.map((value) => ({
    value,
    icon: THEME_ICONS[value],
    label: THEME_LABELS[value],
  }));
  /**
   * Switching language reloads the page, which would lose a layout change the host does not hold: one pending or
   * saving, and one a failed save or a conflict left behind, neither of which saves until the operator acts. Once
   * saved, or when nothing can be saved this session, the reload costs nothing.
   */
  readonly canSwitchLanguage = computed(() => ['saved', 'unavailable'].includes(this.workspace.saveState().kind));
  readonly exportUrl = WORKSPACE_EXPORT_URL;
  /** The download is the host's saved copy, so it waits for a layout change to be saved: it would miss it otherwise. */
  readonly canExport = computed(() => this.workspace.saveState().kind === 'saved');
  /** A chosen file waiting for the operator to say it may replace the workspace (spec §6.2: "after the SPA asks"). */
  readonly pendingImport = signal<{ name: string; document: unknown } | null>(null);
  /** On error, fall back to undefined so the shell (nav, router outlet) still renders without the backend-dir hint. */
  readonly config = toSignal(this.host.config().pipe(catchError(() => of(undefined))));

  constructor() {
    this.workspace.load();
    void this.assistant.load();
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
    const added = this.workspace.addWidget(pageId, {
      type: kind.type,
      cols: kind.defaultSize.cols,
      rows: kind.defaultSize.rows,
      config: structuredClone(preset.config),
    });
    if (!added) {
      this.snackBar.open(
        $localize`This page has no room below its last widget: remove or shrink one first.`,
        $localize`:dismisses a notice:OK`,
        { duration: 8000 },
      );
    }
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
      .open($localize`Deleted "${page.title}:title:"`, $localize`Undo`, { duration: 8000 })
      .onAction()
      .subscribe(() => {
        this.workspace.restorePage(page, at);
        void this.router.navigate(this.workspace.pageLink(page.id));
      });
  }

  /** Reads the chosen file and asks before anything is replaced; a file that is not JSON never gets as far as the host. */
  async chooseImport(input: HTMLInputElement): Promise<void> {
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    try {
      this.pendingImport.set({ name: file.name, document: JSON.parse(await file.text()) });
    } catch {
      this.notify($localize`"${file.name}:file:" is not a workspace file: it is not JSON.`);
    }
  }

  confirmImport(): void {
    const pending = this.pendingImport();
    if (!pending) return;
    this.pendingImport.set(null);
    this.workspace.importWorkspace(pending.document).subscribe({
      next: () => {
        const page = this.workspace.pages().find((p) => p.id === this.workspace.workspace().activePageId && !p.hidden);
        const open = page ?? this.workspace.visiblePages()[0];
        if (open) void this.router.navigate(this.workspace.pageLink(open.id));
        this.notify($localize`Workspace replaced by "${pending.name}:file:".`);
      },
      error: (e: unknown) => this.notify($localize`"${pending.name}:file:" was not imported: ${describe(e)}:reason:`),
    });
  }

  private notify(message: string): void {
    this.snackBar.open(message, $localize`:dismisses a notice:OK`, { duration: 8000 });
  }

  dropped(event: CdkDragDrop<unknown>): void {
    const visible = this.workspace.visiblePages();
    const moved = visible[event.previousIndex];
    const target = visible[event.currentIndex];
    if (moved && target) this.workspace.movePage(moved.id, target.id);
  }
}
