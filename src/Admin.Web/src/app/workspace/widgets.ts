import { Type } from '@angular/core';
import { SCREENS } from '../screens';
import { isScreenPath } from './screen-components';
import { ARCHITECTURE_NOTE, BLANK_NOTE, TAB_ORDER_NOTE } from './widgets/note/note-presets';
import { positionsOf } from './widgets/topology/topology-graph';
import { FieldKind, stringList } from './widget-config';

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

/** A setting a preset list cannot hold, edited by the header's configure control (see `FieldKind`). */
export interface WidgetField {
  key: string;
  label: string;
  kind: FieldKind;
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
  /**
   * False for a type whose presets are starting points rather than settings: choosing one replaces what the operator
   * wrote, so the header's configure control does not offer them (a note edits in place instead).
   */
  configurable?: boolean;
  /** Settings the presets cannot hold; the configure control edits them beside the presets. */
  fields?: readonly WidgetField[];
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

type Kind = Omit<WidgetType, 'config' | 'titleOf' | 'presets'>;

/** A type with nothing to configure: every stored value is dropped, and its one preset adds it as it is. */
function plain(kind: Kind): WidgetType {
  return { ...kind, config: () => ({}), titleOf: () => kind.title, presets: [{ label: kind.title, config: {} }] };
}

/** A type configured by one list of names to show, where `[]`, its preset, is every one. */
function listing(key: string, every: string, kind: Kind): WidgetType {
  return {
    ...kind,
    config: (stored) => ({ [key]: stringList(stored[key]) }),
    titleOf: (config) => {
      const shown = stringList(config[key]);
      return shown.length === 0 ? kind.title : `${kind.title} (${shown.join(', ')})`;
    },
    presets: [{ label: every, config: { [key]: [] } }],
    fields: [{ key, label: `${key[0].toUpperCase()}${key.slice(1)}`, kind: 'names' }],
  };
}

/** The Stack page's widgets (§5.2), which read `StackFeed` and `ComposeRun` in features/stack/stack-feed.ts. */
export const STACK_WIDGETS = {
  doctor: plain({
    type: 'workstation-doctor',
    title: 'Workstation doctor',
    component: () =>
      import('../features/stack/widgets/workstation-doctor-widget').then((m) => m.WorkstationDoctorWidget),
    defaultSize: { cols: 12, rows: 10 },
    minSize: { cols: 6, rows: 4 },
    dataServices: [],
  }),
  controls: plain({
    type: 'stack-controls',
    title: 'Stack controls',
    component: () => import('../features/stack/widgets/stack-controls-widget').then((m) => m.StackControlsWidget),
    defaultSize: { cols: 12, rows: 7 },
    minSize: { cols: 6, rows: 3 },
    dataServices: ['StackFeed', 'ComposeRun', 'PageJobs'],
  }),
  services: listing('services', 'All services', {
    type: 'compose-services',
    title: 'Compose services',
    component: () => import('../features/stack/widgets/compose-services-widget').then((m) => m.ComposeServicesWidget),
    defaultSize: { cols: 12, rows: 12 },
    minSize: { cols: 6, rows: 3 },
    dataServices: ['StackFeed'],
  }),
  reachability: listing('surfaces', 'All surfaces', {
    type: 'reachability',
    title: 'Reachability',
    component: () => import('../features/stack/widgets/reachability-widget').then((m) => m.ReachabilityWidget),
    defaultSize: { cols: 24, rows: 3 },
    minSize: { cols: 4, rows: 2 },
    dataServices: ['StackFeed'],
  }),
  signals: listing('services', 'All services', {
    type: 'golden-signals',
    title: 'Golden signals',
    component: () => import('../features/stack/widgets/golden-signals-widget').then((m) => m.GoldenSignalsWidget),
    defaultSize: { cols: 14, rows: 8 },
    minSize: { cols: 6, rows: 3 },
    dataServices: ['StackFeed'],
  }),
  alerts: {
    type: 'alerts',
    title: 'Alerts',
    component: () => import('../features/stack/widgets/alerts-widget').then((m) => m.AlertsWidget),
    defaultSize: { cols: 10, rows: 8 },
    minSize: { cols: 6, rows: 3 },
    config: (stored) => ({ firingOnly: stored['firingOnly'] === true }),
    titleOf: (config) => (config['firingOnly'] === true ? 'Alerts (firing)' : 'Alerts'),
    presets: [
      { label: 'Every rule', config: { firingOnly: false } },
      { label: 'Firing only', config: { firingOnly: true } },
    ],
    dataServices: ['StackFeed'],
  } satisfies WidgetType,
  loadRun: plain({
    type: 'load-run',
    title: 'Load run',
    component: () => import('../features/stack/widgets/load-run-widget').then((m) => m.LoadRunWidget),
    defaultSize: { cols: 10, rows: 8 },
    minSize: { cols: 6, rows: 4 },
    dataServices: ['StackFeed', 'PageJobs'],
  }),
  client: plain({
    type: 'reference-client',
    title: 'Reference client',
    component: () => import('../features/stack/widgets/reference-client-widget').then((m) => m.ReferenceClientWidget),
    defaultSize: { cols: 14, rows: 12 },
    minSize: { cols: 6, rows: 4 },
    dataServices: ['StackFeed', 'PageJobs'],
  }),
} as const;

/** The Scenario page's widgets (§5.2), each a view of the one run `ScenarioRunner` in features/scenario holds. */
export const SCENARIO_WIDGETS = {
  scenario: plain({
    type: 'scenario',
    title: 'Scenario',
    component: () => import('../features/scenario/widgets/scenario-widget').then((m) => m.ScenarioWidget),
    defaultSize: { cols: 16, rows: 20 },
    minSize: { cols: 8, rows: 6 },
    dataServices: ['ScenarioRunner'],
  }),
  saga: plain({
    type: 'saga-strip',
    title: 'Saga strip',
    component: () => import('../features/scenario/widgets/saga-strip-widget').then((m) => m.SagaStripWidget),
    defaultSize: { cols: 8, rows: 14 },
    minSize: { cols: 5, rows: 5 },
    dataServices: ['ScenarioRunner'],
  }),
} as const;

const FILTER: WidgetField = { key: 'filter', label: 'Filter', kind: 'text' };
const AUTO_REFRESH: WidgetField = { key: 'autoRefresh', label: 'Refresh every 5 s', kind: 'flag' };

/**
 * A Broker listing (§5.2): a text filter, any flags of its own, and the opt-in auto-refresh, which is off until
 * switched on and saved with the widget (§4.3). Its one preset adds it as it is; the settings editor changes the rest.
 */
function brokerListing(kind: Kind, flags: readonly WidgetField[] = []): WidgetType {
  const fields = [FILTER, ...flags, AUTO_REFRESH];
  const config = (stored: Record<string, unknown>): Record<string, unknown> =>
    Object.fromEntries(
      fields.map((f) => [
        f.key,
        f.kind === 'flag' ? stored[f.key] === true : typeof stored[f.key] === 'string' ? stored[f.key] : '',
      ]),
    );
  return {
    ...kind,
    config,
    titleOf: (stored) =>
      typeof stored['filter'] === 'string' && stored['filter'] !== ''
        ? `${kind.title} (${stored['filter']})`
        : kind.title,
    presets: [{ label: kind.title, config: config({}) }],
    fields,
  };
}

/** The Broker page's widgets (§5.2), which read `BrokerFeed` in features/broker/broker-feed.ts. */
export const BROKER_WIDGETS = {
  queues: brokerListing(
    {
      type: 'queues',
      title: 'Queues',
      component: () => import('../features/broker/widgets/queues-widget').then((m) => m.QueuesWidget),
      defaultSize: { cols: 14, rows: 12 },
      minSize: { cols: 8, rows: 4 },
      dataServices: ['BrokerFeed'],
    },
    [{ key: 'parkedOnly', label: 'Parked queues only', kind: 'flag' }],
  ),
  exchanges: brokerListing({
    type: 'exchanges',
    title: 'Exchanges',
    component: () => import('../features/broker/widgets/exchanges-widget').then((m) => m.ExchangesWidget),
    defaultSize: { cols: 10, rows: 8 },
    minSize: { cols: 5, rows: 3 },
    dataServices: ['BrokerFeed'],
  }),
  bindings: brokerListing({
    type: 'bindings',
    title: 'Bindings',
    component: () => import('../features/broker/widgets/bindings-widget').then((m) => m.BindingsWidget),
    defaultSize: { cols: 14, rows: 10 },
    minSize: { cols: 8, rows: 3 },
    dataServices: ['BrokerFeed'],
  }),
  permissions: brokerListing({
    type: 'permissions',
    title: 'Permissions',
    component: () => import('../features/broker/widgets/permissions-widget').then((m) => m.PermissionsWidget),
    defaultSize: { cols: 10, rows: 6 },
    minSize: { cols: 6, rows: 3 },
    dataServices: ['BrokerFeed'],
  }),
  projection: plain({
    type: 'bff-projection',
    title: 'BFF projection',
    component: () => import('../features/broker/widgets/bff-projection-widget').then((m) => m.BffProjectionWidget),
    defaultSize: { cols: 10, rows: 12 },
    minSize: { cols: 6, rows: 5 },
    dataServices: ['BrokerFeed'],
  }),
  deadLetters: {
    type: 'dead-letters',
    title: 'Dead-letter workbench',
    component: () => import('../features/broker/widgets/dead-letter-widget').then((m) => m.DeadLetterWidget),
    defaultSize: { cols: 24, rows: 12 },
    minSize: { cols: 8, rows: 4 },
    config: (stored) => ({
      queue: typeof stored['queue'] === 'string' && stored['queue'] !== '' ? stored['queue'] : null,
    }),
    titleOf: (config) =>
      typeof config['queue'] === 'string' ? `Dead letters (${config['queue']})` : 'Dead-letter workbench',
    presets: [{ label: 'Opened from a Queues widget', config: { queue: null } }],
    fields: [{ key: 'queue', label: 'Queue', kind: 'text' }],
    dataServices: ['BrokerFeed'],
  } satisfies WidgetType,
} as const;

/** The Platform map's widget (§5.2): the table of services, routes and queues, in `MapFeed`; its auto-refresh is opt-in (§4.3). */
export const MAP: WidgetType = {
  type: 'platform-map',
  title: 'Platform map',
  component: () => import('../features/map/widgets/map-widget').then((m) => m.MapWidget),
  defaultSize: { cols: 24, rows: 22 },
  minSize: { cols: 8, rows: 5 },
  config: (stored) => ({ autoRefresh: stored['autoRefresh'] === true }),
  titleOf: () => 'Platform map',
  presets: [{ label: 'Platform map', config: { autoRefresh: false } }],
  fields: [AUTO_REFRESH],
  dataServices: ['MapFeed'],
};

/**
 * The platform drawn as a graph (§8.1), from `MapFeed` and `StackFeed`, with no built-in page. Config: `autoRefresh`, on
 * until switched off (the one widget whose refresh is on by default, as the spec asks), and `positions`, where the operator
 * dragged a node to, which the widget writes and Re-layout empties.
 */
export const TOPOLOGY: WidgetType = {
  type: 'platform-diagram',
  title: 'Platform diagram',
  component: () => import('./widgets/topology/topology-widget').then((m) => m.TopologyWidget),
  defaultSize: { cols: 24, rows: 26 },
  minSize: { cols: 10, rows: 10 },
  config: (stored) => ({ autoRefresh: stored['autoRefresh'] !== false, positions: positionsOf(stored['positions']) }),
  titleOf: () => 'Platform diagram',
  presets: [{ label: 'Platform diagram', config: { autoRefresh: true, positions: {} } }],
  fields: [AUTO_REFRESH],
  dataServices: ['MapFeed', 'StackFeed', 'BrokerFeed'],
};

/** What the platform sent (§5.2): the mail sink's messages, filtered by a correlation id the widget starts with. */
export const MAIL: WidgetType = {
  type: 'mail',
  title: 'Mail',
  component: () => import('../features/mail/widgets/mail-widget').then((m) => m.MailWidget),
  defaultSize: { cols: 24, rows: 18 },
  minSize: { cols: 8, rows: 5 },
  config: (stored) => ({ correlationId: typeof stored['correlationId'] === 'string' ? stored['correlationId'] : '' }),
  titleOf: (config) =>
    typeof config['correlationId'] === 'string' && config['correlationId'] !== ''
      ? `Mail (${config['correlationId']})`
      : 'Mail',
  presets: [{ label: 'Every message', config: { correlationId: '' } }],
  fields: [{ key: 'correlationId', label: 'Correlation id', kind: 'text' }],
  dataServices: [],
};

/** An erasure request raised and watched (§5.2); the request is held by `ErasureRun`, the subject being typed is session state. */
export const ERASURE: WidgetType = plain({
  type: 'erasure',
  title: 'Erasure',
  component: () => import('../features/erasure/widgets/erasure-widget').then((m) => m.ErasureWidget),
  defaultSize: { cols: 24, rows: 20 },
  minSize: { cols: 10, rows: 6 },
  dataServices: ['ErasureRun'],
});

/**
 * The API page's widgets (§5.2). The request widget saves the operation it is on and the identity by username only
 * (`anonymous`, `user:<name>`, `custom:<name>`): a custom identity's password is session state and is never in a config
 * (§6.4). The history widget lists what the tab sent, from `ApiHistory`.
 */
export const API_WIDGETS = {
  request: {
    type: 'api-request',
    title: 'API request',
    component: () => import('../features/api/widgets/api-request-widget').then((m) => m.ApiRequestWidget),
    defaultSize: { cols: 16, rows: 30 },
    minSize: { cols: 8, rows: 8 },
    config: (stored) => ({
      operation: typeof stored['operation'] === 'string' && stored['operation'] !== '' ? stored['operation'] : null,
      identity:
        typeof stored['identity'] === 'string' && /^(anonymous|user:.+|custom:.*)$/.test(stored['identity'])
          ? stored['identity']
          : null,
    }),
    titleOf: () => 'API request',
    presets: [{ label: 'API request', config: { operation: null, identity: null } }],
    fields: [
      { key: 'operation', label: 'Operation id', kind: 'text' },
      { key: 'identity', label: 'Identity: anonymous, user:<name> or custom:<name>', kind: 'text' },
    ],
    dataServices: ['ApiHistory'],
  } satisfies WidgetType,
  history: plain({
    type: 'api-history',
    title: 'API history',
    component: () => import('../features/api/widgets/api-history-widget').then((m) => m.ApiHistoryWidget),
    defaultSize: { cols: 8, rows: 30 },
    minSize: { cols: 4, rows: 4 },
    dataServices: ['ApiHistory'],
  }),
} as const;

/**
 * One correlation id's timeline (§5.2, §5.9). Config: the id and window it starts with, and `follow`, which makes a
 * correlation-id link anywhere on the page re-point it (`TraceFollow`) instead of opening the Trace page. The built-in
 * Trace page's widget follows and reads the route's id.
 */
export const TRACE: WidgetType = {
  type: 'trace',
  title: 'Trace',
  component: () => import('../features/trace/widgets/trace-widget').then((m) => m.TraceWidget),
  defaultSize: { cols: 24, rows: 20 },
  minSize: { cols: 8, rows: 6 },
  config: (stored) => ({
    correlationId: typeof stored['correlationId'] === 'string' ? stored['correlationId'] : '',
    window: ['15m', '1h', '6h', '24h'].includes(stored['window'] as string) ? stored['window'] : '15m',
    follow: stored['follow'] === true,
  }),
  titleOf: (config) =>
    typeof config['correlationId'] === 'string' && config['correlationId'] !== ''
      ? `Trace (${config['correlationId']})`
      : 'Trace',
  presets: [
    { label: 'Trace', config: { correlationId: '', window: '15m', follow: false } },
    { label: 'Follows the last Trace link', config: { correlationId: '', window: '15m', follow: true } },
  ],
  fields: [
    { key: 'correlationId', label: 'Correlation id, traced when the widget appears or this changes', kind: 'text' },
    { key: 'window', label: 'Window: 15m, 1h, 6h or 24h', kind: 'text' },
    { key: 'follow', label: 'Follow the last Trace link', kind: 'flag' },
  ],
  dataServices: ['TraceHistory', 'TraceFollow'],
};

/**
 * Where one request went (§8.2): a Mermaid sequence diagram of a correlation id's Trace timeline. Config as the Trace
 * widget's: the id and window it starts with, and `follow`. It is drawn when asked and never polled.
 */
export const SEQUENCE: WidgetType = {
  type: 'sequence',
  title: 'Request sequence',
  component: () => import('./widgets/sequence/sequence-widget').then((m) => m.SequenceWidget),
  defaultSize: { cols: 16, rows: 24 },
  minSize: { cols: 8, rows: 8 },
  config: (stored) => ({
    correlationId: typeof stored['correlationId'] === 'string' ? stored['correlationId'] : '',
    window: ['15m', '1h', '6h', '24h'].includes(stored['window'] as string) ? stored['window'] : '15m',
    follow: stored['follow'] === true,
  }),
  titleOf: (config) =>
    typeof config['correlationId'] === 'string' && config['correlationId'] !== ''
      ? `Request sequence (${config['correlationId']})`
      : 'Request sequence',
  presets: [
    { label: 'Request sequence', config: { correlationId: '', window: '15m', follow: false } },
    { label: 'Follows the last Trace link', config: { correlationId: '', window: '15m', follow: true } },
  ],
  fields: [
    { key: 'correlationId', label: 'Correlation id, drawn when the widget appears or this changes', kind: 'text' },
    { key: 'window', label: 'Window: 15m, 1h, 6h or 24h', kind: 'text' },
    { key: 'follow', label: 'Follow the last Trace link', kind: 'flag' },
  ],
  dataServices: ['TraceHistory', 'TraceFollow'],
};

/**
 * The Runbooks page's widget (§5.2): the backend's runbooks with the script that causes each alert. Config: `runbook`,
 * the one it opens on. Which is open, the alert to cause and the job being shown are session state; the first press of
 * a cause and the typed name that unlocks one are never kept (§4.2).
 */
export const RUNBOOKS: WidgetType = {
  type: 'runbooks',
  title: 'Runbooks',
  component: () => import('../features/runbooks/widgets/runbooks-widget').then((m) => m.RunbooksWidget),
  defaultSize: { cols: 24, rows: 26 },
  minSize: { cols: 10, rows: 8 },
  config: (stored) => ({
    runbook: typeof stored['runbook'] === 'string' && stored['runbook'] !== '' ? stored['runbook'] : null,
  }),
  titleOf: (config) => (typeof config['runbook'] === 'string' ? `Runbooks (${config['runbook']})` : 'Runbooks'),
  presets: [{ label: 'Runbooks', config: { runbook: null } }],
  fields: [{ key: 'runbook', label: 'Runbook to open on', kind: 'text' }],
  dataServices: [],
};

/** What the operator did this session (§5.2): the host's scrubbed transcript, read from `TranscriptFeed`. */
export const TRANSCRIPT: WidgetType = plain({
  type: 'transcript',
  title: 'Transcript',
  component: () => import('../features/transcript/widgets/transcript-widget').then((m) => m.TranscriptWidget),
  defaultSize: { cols: 24, rows: 20 },
  minSize: { cols: 8, rows: 5 },
  dataServices: ['TranscriptFeed'],
});

/** The Logs page's widget (§5.2), a view of the one follow `LogFollowService` in features/logs runs. */
export const LOGS: WidgetType = listing('services', 'All services', {
  type: 'logs',
  title: 'Logs',
  component: () => import('../features/logs/widgets/logs-widget').then((m) => m.LogsWidget),
  defaultSize: { cols: 24, rows: 18 },
  minSize: { cols: 8, rows: 6 },
  dataServices: ['LogFollowService'],
});

/** Every screen's output pane, as a widget of its own (§5.2): a job by id, or the job its page started last. */
export const JOB_OUTPUT: WidgetType = {
  type: 'job-output',
  title: 'Job output',
  component: () => import('./job-output-widget').then((m) => m.JobOutputWidget),
  defaultSize: { cols: 12, rows: 7 },
  minSize: { cols: 4, rows: 3 },
  config: (stored) => ({ jobId: typeof stored['jobId'] === 'string' ? stored['jobId'] : null }),
  titleOf: (config) => (typeof config['jobId'] === 'string' ? `Job output (${config['jobId']})` : 'Job output'),
  presets: [{ label: 'The job this page started last', config: { jobId: null } }],
  fields: [{ key: 'jobId', label: 'Job id', kind: 'text' }],
  dataServices: ['PageJobs'],
};

/** A note or a diagram (§8.3): Markdown whose mermaid fences are drawn, written in place while editing the layout. */
export const NOTE: WidgetType = {
  type: 'note',
  title: 'Note',
  component: () => import('./widgets/note/note-widget').then((m) => m.NoteWidget),
  defaultSize: { cols: 12, rows: 12 },
  minSize: { cols: 4, rows: 3 },
  config: (stored) => ({ source: typeof stored['source'] === 'string' ? stored['source'] : '' }),
  titleOf: (config) => {
    const source = typeof config['source'] === 'string' ? config['source'] : '';
    // The first heading the note shows: a `#` line inside a code fence is code, as `blocks` reads it.
    let fenced = false;
    for (const line of source.split(/\r?\n/)) {
      if (line.trimStart().startsWith('```')) fenced = !fenced;
      else if (!fenced) {
        const heading = /^#{1,6}[ \t]+(.+?)[ \t]*$/.exec(line);
        if (heading) return heading[1];
      }
    }
    return 'Note';
  },
  presets: [
    { label: 'Empty note', config: { source: '' } },
    { label: 'Note with a diagram', config: { source: BLANK_NOTE } },
    { label: 'Architecture diagram', config: { source: ARCHITECTURE_NOTE } },
    { label: 'Work order of the tabs', config: { source: TAB_ORDER_NOTE } },
  ],
  configurable: false,
  dataServices: [],
};

/**
 * Every widget type, read by the "Add widget" picker, the grid and the built-in pages. A type unknown to this list is
 * rendered as "unknown widget" and saved back untouched, so a document from a newer console survives an older one (§6.3).
 */
export const WIDGETS: readonly WidgetType[] = [
  ...Object.values(STACK_WIDGETS),
  ...Object.values(SCENARIO_WIDGETS),
  ...Object.values(BROKER_WIDGETS),
  ...Object.values(API_WIDGETS),
  TRACE,
  SEQUENCE,
  RUNBOOKS,
  TRANSCRIPT,
  MAP,
  TOPOLOGY,
  MAIL,
  ERASURE,
  LOGS,
  JOB_OUTPUT,
  NOTE,
  LEGACY_SCREEN,
];

export function widgetType(type: string): WidgetType | undefined {
  return WIDGETS.find((w) => w.type === type);
}
