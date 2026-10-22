import { Type } from '@angular/core';
import { SCREENS } from '../screens';
import { isScreenPath } from './screen-components';

/** A size in grid cells. */
export interface CellSize {
  cols: number;
  rows: number;
}

/** A ready-made config the "Add widget" picker offers for a type. */
export interface WidgetPreset {
  label: string;
  config: Record<string, unknown>;
}

/**
 * One widget type (widget-workspace spec §5.1). Its component takes one input, `config`, already through `config`
 * below; a stored value the type does not accept falls back to its default rather than failing the page (§6.3).
 */
export interface WidgetType {
  type: string;
  title: string;
  component: () => Promise<Type<unknown>>;
  defaultSize: CellSize;
  minSize: CellSize;
  /** The stored config, with every value this type does not accept replaced by its default. */
  config: (stored: Record<string, unknown>) => Record<string, unknown>;
  /** The header an instance shows, which can name what its config chose. */
  titleOf: (config: Record<string, unknown>) => string;
  presets: readonly WidgetPreset[];
  /** The data services the widget reads (§4.4); the legacy screen widget's screens own their own calls. */
  dataServices: readonly string[];
}

/** The type that renders one of today's screens whole, so every built-in page exists from the first phase (§10). */
export const LEGACY_SCREEN: WidgetType = {
  type: 'legacy',
  title: 'Screen',
  component: () => import('./legacy-screen-widget').then((m) => m.LegacyScreenWidget),
  defaultSize: { cols: 24, rows: 16 },
  minSize: { cols: 6, rows: 4 },
  config: (stored) => ({ screen: isScreenPath(stored['screen']) ? stored['screen'] : SCREENS[0].path }),
  titleOf: (config) => SCREENS.find((s) => s.path === config['screen'])?.label ?? 'Screen',
  presets: SCREENS.map((s) => ({ label: s.label, config: { screen: s.path } })),
  dataServices: [],
};

/**
 * Every widget type, read by the "Add widget" picker, the grid and the built-in pages. A type unknown to this list is rendered as "unknown widget" and saved back
 * untouched, so a document from a newer console survives an older one (§6.3).
 */
export const WIDGETS: readonly WidgetType[] = [LEGACY_SCREEN];

export function widgetType(type: string): WidgetType | undefined {
  return WIDGETS.find((w) => w.type === type);
}
