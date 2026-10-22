import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { catchError, of } from 'rxjs';
import { HostClient } from './core/host/host-client';
import { THEME_CHOICES, ThemeService } from './core/theme/theme-service';
import { SCREENS } from './screens';
import { CommandPalette } from './shared/command-palette/command-palette';

/** The icon (a Material Symbols ligature) each theme choice shows on the top bar's switch. */
const THEME_ICONS = { system: 'brightness_auto', light: 'light_mode', dark: 'dark_mode' } as const;

@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatButtonToggleModule,
    MatIconModule,
    MatListModule,
    MatSidenavModule,
    MatToolbarModule,
    CommandPalette,
  ],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly host = inject(HostClient);
  readonly theme = inject(ThemeService);
  readonly screens = SCREENS;
  readonly themeChoices = THEME_CHOICES.map((value) => ({ value, icon: THEME_ICONS[value] }));
  /** On error, fall back to undefined so the shell (nav, router outlet) still renders without the backend-dir hint. */
  readonly config = toSignal(this.host.config().pipe(catchError(() => of(undefined))));
}
