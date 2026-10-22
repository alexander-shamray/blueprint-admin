import { Routes } from '@angular/router';
import { SCREENS } from './screens';

const workspacePage = () => import('./workspace/workspace-page').then((m) => m.WorkspacePage);

/**
 * Every screen's old path opens its built-in page, and hands the page's widgets its parameters (widget-workspace
 * spec §5.3): `/trace/:correlationId` and `/mail?correlationId=` still land where they did. An operator's own page
 * is `/page/:pageId`.
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: SCREENS[0].path },
  ...SCREENS.map((s) => ({ path: s.path, loadComponent: workspacePage, data: { builtIn: s.path } })),
  { path: 'trace/:correlationId', loadComponent: workspacePage, data: { builtIn: 'trace' } },
  { path: 'page/:pageId', loadComponent: workspacePage },
];
