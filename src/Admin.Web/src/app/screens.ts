/**
 * The console's screens, in their default order: the routes, the built-in pages, the legacy screen widget's choices
 * and the host's `WorkspaceDefaults` (held to this list by a drift test) all follow it. The side navigation and the
 * palette list the operator's pages instead (widget-workspace spec §7.2).
 */
export const SCREENS = [
  { path: 'stack', label: 'Stack' },
  { path: 'logs', label: 'Logs' },
  { path: 'broker', label: 'Broker' },
  { path: 'map', label: 'Map' },
  { path: 'erasure', label: 'Erasure' },
  { path: 'mail', label: 'Mail' },
  { path: 'requests', label: 'API' },
  { path: 'trace', label: 'Trace' },
  { path: 'scenario', label: 'Scenario' },
  { path: 'runbooks', label: 'Runbooks' },
  { path: 'transcript', label: 'Transcript' },
] as const;
