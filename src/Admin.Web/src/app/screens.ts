/**
 * The console's screens, in the order the bar shows them: one list for the bar and the command palette, so a
 * screen added to one is never missing from the other. The paths are app.routes.ts's.
 */
export const SCREENS = [
  { path: 'stack', label: 'Stack' },
  { path: 'logs', label: 'Logs' },
  { path: 'broker', label: 'Broker' },
  { path: 'requests', label: 'API' },
  { path: 'trace', label: 'Trace' },
  { path: 'scenario', label: 'Scenario' },
] as const;
