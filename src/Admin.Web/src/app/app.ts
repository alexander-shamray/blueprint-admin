import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';
import { HostClient } from './core/host/host-client';
import { SCREENS } from './screens';
import { CommandPalette } from './shared/command-palette/command-palette';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, CommandPalette],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly host = inject(HostClient);
  readonly screens = SCREENS;
  /** On error, fall back to undefined so the shell (nav, router outlet) still renders without the backend-dir hint. */
  readonly config = toSignal(this.host.config().pipe(catchError(() => of(undefined))));
}
