import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'stack' },
  { path: 'stack', loadComponent: () => import('./features/stack/stack-page').then((m) => m.StackPage) },
  { path: 'logs', loadComponent: () => import('./features/logs/logs-page').then((m) => m.LogsPage) },
  { path: 'api', loadComponent: () => import('./features/api/api-page').then((m) => m.ApiPage) },
];
