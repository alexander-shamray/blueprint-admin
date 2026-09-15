import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'stack' },
  { path: 'stack', loadComponent: () => import('./features/stack/stack-page').then((m) => m.StackPage) },
  { path: 'logs', loadComponent: () => import('./features/logs/logs-page').then((m) => m.LogsPage) },
  { path: 'broker', loadComponent: () => import('./features/broker/broker-page').then((m) => m.BrokerPage) },
  { path: 'requests', loadComponent: () => import('./features/api/api-page').then((m) => m.ApiPage) },
];
