import { Type } from '@angular/core';
import { SCREENS } from '../screens';

export type ScreenPath = (typeof SCREENS)[number]['path'];

/**
 * Today's eleven screen components, lazy-loaded, keyed by the screen's path in SCREENS. The legacy screen widget
 * renders one of them whole (widget-workspace spec §10, phase 1); the map is typed by SCREENS, so a screen added
 * there without a component here does not compile.
 */
export const SCREEN_COMPONENTS: Record<ScreenPath, () => Promise<Type<unknown>>> = {
  stack: () => import('../features/stack/stack-page').then((m) => m.StackPage),
  logs: () => import('../features/logs/logs-page').then((m) => m.LogsPage),
  broker: () => import('../features/broker/broker-page').then((m) => m.BrokerPage),
  map: () => import('../features/map/map-page').then((m) => m.MapPage),
  erasure: () => import('../features/erasure/erasure-page').then((m) => m.ErasurePage),
  mail: () => import('../features/mail/mail-page').then((m) => m.MailPage),
  requests: () => import('../features/api/api-page').then((m) => m.ApiPage),
  trace: () => import('../features/trace/trace-page').then((m) => m.TracePage),
  scenario: () => import('../features/scenario/scenario-page').then((m) => m.ScenarioPage),
  runbooks: () => import('../features/runbooks/runbooks-page').then((m) => m.RunbooksPage),
  transcript: () => import('../features/transcript/transcript-page').then((m) => m.TranscriptPage),
};

export function isScreenPath(value: unknown): value is ScreenPath {
  return typeof value === 'string' && SCREENS.some((s) => s.path === value);
}
