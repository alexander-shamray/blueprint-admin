import { Injectable, computed, inject, signal } from '@angular/core';
import { HostClient } from '../host/host-client';
import { Identity, RealmUserView } from '../host/host-types';

export type IdentityChoice =
  | { kind: 'anonymous' }
  | { kind: 'user'; username: string }
  | { kind: 'custom'; username: string; password: string };

/** Who the API screen sends as. Root-scoped so the choice survives leaving and returning to the screen. */
@Injectable({ providedIn: 'root' })
export class IdentityState {
  private readonly host = inject(HostClient);
  private chosen = false;
  private readonly selected = signal<IdentityChoice>({ kind: 'anonymous' });

  readonly users = signal<RealmUserView[]>([]);
  readonly choice = this.selected.asReadonly();

  /** An explicit choice, which a users load that answers later does not overwrite. */
  select(choice: IdentityChoice): void {
    this.chosen = true;
    this.selected.set(choice);
  }

  readonly request = computed<Identity | null>(() => {
    const c = this.selected();
    switch (c.kind) {
      case 'anonymous':
        return null;
      case 'user':
        return { username: c.username, password: null };
      case 'custom':
        return { username: c.username, password: c.password };
    }
  });

  readonly label = computed(() => {
    const c = this.selected();
    return c.kind === 'anonymous' ? 'anonymous' : c.kind === 'user' ? c.username : `${c.username} (custom)`;
  });

  load(): void {
    this.host.identityUsers().subscribe({
      next: (users) => {
        this.users.set(users);
        const demo = users.find((u) => u.username === 'demo');
        if (!this.chosen && demo) {
          this.selected.set({ kind: 'user', username: demo.username });
        }
      },
      // Keep the last list: emptying it would hide the chosen user's option while requests still go out as that user.
      error: () => undefined,
    });
  }
}
