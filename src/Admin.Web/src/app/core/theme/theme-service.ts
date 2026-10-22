import { DOCUMENT } from '@angular/common';
import { Injectable, effect, inject, signal } from '@angular/core';

export type ThemeChoice = 'system' | 'light' | 'dark';

export const THEME_CHOICES: readonly ThemeChoice[] = ['system', 'light', 'dark'];

/** The browser-local key; the choice is a per-viewer convenience, so it is never sent to the host. */
export const THEME_STORAGE_KEY = 'admin.theme';

/**
 * The operator's light/dark choice. `system` removes the attribute and leaves `color-scheme` to the browser; the
 * other two set `data-theme` on <html>, which styles.css turns into `color-scheme`, and the Material tokens
 * (theme.scss) follow it. Storage can be missing or throw (private window, blocked site data), so the service
 * works without it and the choice then lasts the page's life.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly root = inject(DOCUMENT).documentElement;
  readonly choice = signal<ThemeChoice>(readStored());

  constructor() {
    effect(() => {
      const choice = this.choice();
      if (choice === 'system') this.root.removeAttribute('data-theme');
      else this.root.setAttribute('data-theme', choice);
    });
  }

  set(choice: ThemeChoice): void {
    this.choice.set(choice);
    try {
      localStorage.setItem(THEME_STORAGE_KEY, choice);
    } catch {
      // Storage is a convenience; the choice already applies.
    }
  }
}

function readStored(): ThemeChoice {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY);
    return THEME_CHOICES.find((c) => c === stored) ?? 'system';
  } catch {
    return 'system';
  }
}
