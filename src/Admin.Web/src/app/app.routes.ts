import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'stack' },
  { path: 'stack', loadComponent: () => import('./features/stack/stack-page').then((m) => m.StackPage) },
  { path: 'logs', loadComponent: () => import('./features/logs/logs-page').then((m) => m.LogsPage) },
  { path: 'broker', loadComponent: () => import('./features/broker/broker-page').then((m) => m.BrokerPage) },
  { path: 'map', loadComponent: () => import('./features/map/map-page').then((m) => m.MapPage) },
  { path: 'erasure', loadComponent: () => import('./features/erasure/erasure-page').then((m) => m.ErasurePage) },
  { path: 'mail', loadComponent: () => import('./features/mail/mail-page').then((m) => m.MailPage) },
  { path: 'requests', loadComponent: () => import('./features/api/api-page').then((m) => m.ApiPage) },
  { path: 'trace', loadComponent: () => import('./features/trace/trace-page').then((m) => m.TracePage) },
  { path: 'scenario', loadComponent: () => import('./features/scenario/scenario-page').then((m) => m.ScenarioPage) },
  { path: 'runbooks', loadComponent: () => import('./features/runbooks/runbooks-page').then((m) => m.RunbooksPage) },
  {
    path: 'transcript',
    loadComponent: () => import('./features/transcript/transcript-page').then((m) => m.TranscriptPage),
  },
  { path: 'trace/:correlationId', loadComponent: () => import('./features/trace/trace-page').then((m) => m.TracePage) },
];
