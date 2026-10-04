import { Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { ScenarioLauncher } from '../../features/scenario/scenario-launcher';
import { SCREENS } from '../../screens';

/** One thing the palette can do: its words, and what choosing it does. */
export interface PaletteCommand {
  id: string;
  label: string;
  run: () => void;
}

/**
 * Ctrl+K (Cmd+K on a Mac) from anywhere: go to a screen, run the Scenario, or open what was typed as a correlation
 * id on the Trace screen. The id is not judged here: the trace endpoint refuses one the platform would not adopt,
 * by the host's one copy of that rule (CorrelationId.IsAdoptable), which is the LogQL boundary the palette must not
 * be a way round. A native modal dialog keeps focus inside it and gives Escape for free. It is a keyboard tool: focus
 * stays in the input and the options are named to assistive technology through aria-activedescendant, so they are not
 * focusable and take no pointer; the bar is the pointer's way between screens.
 */
@Component({
  selector: 'app-command-palette',
  templateUrl: './command-palette.html',
  styleUrl: './command-palette.css',
  host: { '(document:keydown)': 'onDocumentKey($event)' },
})
export class CommandPalette {
  private readonly router = inject(Router);
  private readonly launcher = inject(ScenarioLauncher);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly input = viewChild.required<ElementRef<HTMLInputElement>>('input');

  readonly query = signal('');
  readonly active = signal(0);

  private readonly fixed: PaletteCommand[] = [
    ...SCREENS.map((s) => ({ id: `go-${s.path}`, label: `Go to ${s.label}`, run: () => void this.router.navigate(['/', s.path]) })),
    {
      id: 'run-scenario',
      label: 'Run the Scenario',
      run: () => {
        this.launcher.request();
        void this.router.navigate(['/scenario']);
      },
    },
  ];

  /** The fixed commands whose words contain what was typed, then the typed text as a correlation id. */
  readonly commands = computed<PaletteCommand[]>(() => {
    const typed = this.query().trim();
    const words = typed.toLowerCase();
    const matching = this.fixed.filter((c) => c.label.toLowerCase().includes(words));
    if (typed === '') return matching;
    return [
      ...matching,
      { id: 'trace', label: `Open trace for ${typed}`, run: () => void this.router.navigate(['/trace', typed]) },
    ];
  });

  readonly activeId = computed(() => {
    const command = this.commands()[this.active()];
    return command ? `palette-${command.id}` : null;
  });

  onDocumentKey(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      this.open();
    }
  }

  open(): void {
    this.query.set('');
    this.active.set(0);
    const dialog = this.dialog().nativeElement;
    if (!dialog.open) dialog.showModal();
    this.input().nativeElement.focus();
  }

  typed(value: string): void {
    this.query.set(value);
    this.active.set(0);
  }

  onInputKey(event: KeyboardEvent): void {
    const count = this.commands().length;
    if (event.key === 'ArrowDown' && count > 0) {
      event.preventDefault();
      this.active.update((i) => (i + 1) % count);
    } else if (event.key === 'ArrowUp' && count > 0) {
      event.preventDefault();
      this.active.update((i) => (i - 1 + count) % count);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      const command = this.commands()[this.active()];
      if (command) this.execute(command);
    }
  }

  execute(command: PaletteCommand): void {
    this.dialog().nativeElement.close();
    command.run();
  }
}
