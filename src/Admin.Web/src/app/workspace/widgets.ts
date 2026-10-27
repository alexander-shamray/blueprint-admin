import { Type } from '@angular/core';
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
  /**
   * What the assistant explains this widget from (AI assistant design §6.1): two to five sentences on what it shows, what
   * it can do and what it is not for. The widget owns this text; the screen it sits on only names it. Sent in the source
   * language, which the model translates as it answers. A spec fails a type without it.
   */
  help: string;
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
  /** The data services the widget reads (§4.4). */
  dataServices: readonly string[];
}

type Kind = Omit<WidgetType, 'config' | 'titleOf' | 'presets'>;

/** A type with nothing to configure: every stored value is dropped, and its one preset adds it as it is. */
function plain(kind: Kind): WidgetType {
  return { ...kind, config: () => ({}), titleOf: () => kind.title, presets: [{ label: kind.title, config: {} }] };
}

/** The label of each list setting `listing` is given, in the active language. */
const LISTING_LABELS: Record<string, string> = { services: $localize`Services`, surfaces: $localize`Surfaces` };

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
    fields: [{ key, label: LISTING_LABELS[key] ?? key, kind: 'names' }],
  };
}

/** The Stack page's widgets (§5.2), which read `StackFeed` and `ComposeRun` in features/stack/stack-feed.ts. */
export const STACK_WIDGETS = {
  doctor: plain({
    type: 'workstation-doctor',
    title: $localize`Workstation doctor`,
    help:
      "Checks the developer's workstation against what run-locally.md needs before the stack can run, and says what " +
      'is missing or wrong. Use it first when the stack will not start. It reports; it installs or changes nothing.',
    component: () =>
      import('../features/stack/widgets/workstation-doctor-widget').then((m) => m.WorkstationDoctorWidget),
    defaultSize: { cols: 12, rows: 10 },
    minSize: { cols: 6, rows: 4 },
    dataServices: [],
  }),
  controls: plain({
    type: 'stack-controls',
    title: $localize`Stack controls`,
    help:
      'The buttons that start, stop and reset the backend as run-locally.md does: Up, Down, Down with the volumes ' +
      'wiped and Reset, the last two behind a typed confirmation. They are refused while a Scenario holds the stack, ' +
      'and the widget says when a Scenario has changed a service and will undo it. It shows no logs; Logs does.',
    component: () => import('../features/stack/widgets/stack-controls-widget').then((m) => m.StackControlsWidget),
    defaultSize: { cols: 12, rows: 7 },
    minSize: { cols: 6, rows: 3 },
    dataServices: ['StackFeed', 'ComposeRun', 'PageJobs'],
  }),
  services: listing('services', $localize`All services`, {
    type: 'compose-services',
    title: $localize`Compose services`,
    help:
      'Each backend Compose service with its state, its health and its ports, for the services chosen. Use it to see ' +
      'what is running and what is unhealthy. It starts and stops nothing; Stack controls does.',
    component: () => import('../features/stack/widgets/compose-services-widget').then((m) => m.ComposeServicesWidget),
    defaultSize: { cols: 12, rows: 12 },
    minSize: { cols: 6, rows: 3 },
    dataServices: ['StackFeed'],
  }),
  reachability: listing('surfaces', $localize`All surfaces`, {
    type: 'reachability',
    title: $localize`Reachability`,
    help:
      "Whether each platform surface answers: every service's readiness endpoint, the object store, the image edge, " +
      'Keycloak, Grafana and the reference client. Use it to tell a service that is down from one that is up but ' +
      'misbehaving. It checks that a URL answers, not that the service is correct.',
    component: () => import('../features/stack/widgets/reachability-widget').then((m) => m.ReachabilityWidget),
    defaultSize: { cols: 24, rows: 3 },
    minSize: { cols: 4, rows: 2 },
    dataServices: ['StackFeed'],
  }),
  signals: listing('services', $localize`All services`, {
    type: 'golden-signals',
    title: $localize`Golden signals`,
    help:
      "Each service's request rate, error rates (the 401 and 422 among them) and command and query p95 latency from " +
      'Grafana, for the services chosen. Use it to see which service is slow or failing. It shows trends, not ' +
      'individual requests; Trace follows one request.',
    component: () => import('../features/stack/widgets/golden-signals-widget').then((m) => m.GoldenSignalsWidget),
    defaultSize: { cols: 14, rows: 8 },
    minSize: { cols: 6, rows: 3 },
    dataServices: ['StackFeed'],
  }),
  alerts: {
    type: 'alerts',
    title: $localize`Alerts`,
    help:
      "The platform's Prometheus alert rules and which are firing, pending or quiet, with the rules that cannot fire " +
      'because nothing records their metrics yet. It can show only the firing ones. It does not silence an alert; ' +
      'the Runbooks widget can cause one.',
    component: () => import('../features/stack/widgets/alerts-widget').then((m) => m.AlertsWidget),
    defaultSize: { cols: 10, rows: 8 },
    minSize: { cols: 6, rows: 3 },
    config: (stored) => ({ firingOnly: stored['firingOnly'] === true }),
    titleOf: (config) => (config['firingOnly'] === true ? $localize`Alerts (firing)` : $localize`Alerts`),
    presets: [
      { label: $localize`Every rule`, config: { firingOnly: false } },
      { label: $localize`Firing only`, config: { firingOnly: true } },
    ],
    dataServices: ['StackFeed'],
  } satisfies WidgetType,
  loadRun: plain({
    type: 'load-run',
    title: $localize`Load run`,
    help:
      "Runs the backend's k6 SLO load run, the steps of run-locally.md's load run, and shows its status and the last " +
      "run; the output opens in the page's Job output widget, and Stop load stops it. Use it to put the stack under " +
      'traffic and watch the golden signals. It generates real load, so it is for the local stack only.',
    component: () => import('../features/stack/widgets/load-run-widget').then((m) => m.LoadRunWidget),
    defaultSize: { cols: 10, rows: 8 },
    minSize: { cols: 6, rows: 4 },
    dataServices: ['StackFeed', 'PageJobs'],
  }),
  stressRun: plain({
    type: 'stress-run',
    title: $localize`Stress run`,
    help:
      "Runs the backend's k6 stress run, the steps of run-locally.md's stress run: it brings the stack up with the stress " +
      'override, then ramps signed-in users through the gateway in steps until the platform gives out. It shows the ' +
      'step k6 is at, the knee once named, and whether the stack still runs on the override, with the way back. The ' +
      "output opens in the page's Job output widget, and Stop stress stops it. Use it to find which part gives out " +
      'first; it says nothing about production capacity, and it is for the local stack only.',
    component: () => import('../features/stack/widgets/stress-run-widget').then((m) => m.StressRunWidget),
    defaultSize: { cols: 10, rows: 10 },
    minSize: { cols: 6, rows: 5 },
    dataServices: ['StackFeed', 'PageJobs'],
  }),
  client: plain({
    type: 'reference-client',
    title: $localize`Reference client`,
    help:
      'Starts and stops the frontend dev server (npm start in the sibling blueprint-frontend clone) and says whether ' +
      "the client answers; its output opens in the page's Job output widget. It also builds the app for the " +
      'emulator and reads the device. Use it to run the web client against the local backend. It does not edit the ' +
      'frontend.',
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
    title: $localize`Scenario`,
    help:
      'Runs an end-to-end scenario script (an order delivered, cancelled or refused) as a chosen realm user: places ' +
      'an order and follows it through the saga, payment, stock and shipping, checking each step. It can be ' +
      'cancelled and exported as Markdown. Use it to check the platform works as a whole. It holds the stack while ' +
      'it runs and can change a service to cause a failure, which it undoes itself.',
    component: () => import('../features/scenario/widgets/scenario-widget').then((m) => m.ScenarioWidget),
    defaultSize: { cols: 16, rows: 20 },
    minSize: { cols: 8, rows: 6 },
    dataServices: ['ScenarioRunner'],
  }),
  saga: plain({
    type: 'saga-strip',
    title: $localize`Saga strip`,
    help:
      "The order saga of the Scenario's run as it moves, with the state it reads now and when the console first saw it " +
      "so. It reads the run's order and starts nothing. When it stops reading it shows the last state read, not the " +
      "order's state at this moment.",
    component: () => import('../features/scenario/widgets/saga-strip-widget').then((m) => m.SagaStripWidget),
    defaultSize: { cols: 8, rows: 14 },
    minSize: { cols: 5, rows: 5 },
    dataServices: ['ScenarioRunner'],
  }),
} as const;

const FILTER: WidgetField = { key: 'filter', label: $localize`Filter`, kind: 'text' };
const AUTO_REFRESH: WidgetField = { key: 'autoRefresh', label: $localize`Refresh every 5 s`, kind: 'flag' };

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
      title: $localize`Queues`,
      help:
        "Each RabbitMQ queue's depth, the service that consumes it, whether it is a parked (dead-letter) queue, with a " +
        'text filter and an option to show only parked queues. Use it to see where messages are waiting. It publishes ' +
        'or deletes nothing; the Dead-letter workbench replays or discards parked messages.',
      component: () => import('../features/broker/widgets/queues-widget').then((m) => m.QueuesWidget),
      defaultSize: { cols: 14, rows: 12 },
      minSize: { cols: 8, rows: 4 },
      dataServices: ['BrokerFeed'],
    },
    [{ key: 'parkedOnly', label: $localize`Parked queues only`, kind: 'flag' }],
  ),
  exchanges: brokerListing({
    type: 'exchanges',
    title: $localize`Exchanges`,
    help:
      "The broker's exchanges, with a text filter. Use it to see how messages are routed in. It lists them; it " +
      'changes none.',
    component: () => import('../features/broker/widgets/exchanges-widget').then((m) => m.ExchangesWidget),
    defaultSize: { cols: 10, rows: 8 },
    minSize: { cols: 5, rows: 3 },
    dataServices: ['BrokerFeed'],
  }),
  bindings: brokerListing({
    type: 'bindings',
    title: $localize`Bindings`,
    help:
      'The bindings that connect exchanges to queues, with a text filter. Use it to see which queue a message ' +
      'published to an exchange reaches. It lists them; it changes none.',
    component: () => import('../features/broker/widgets/bindings-widget').then((m) => m.BindingsWidget),
    defaultSize: { cols: 14, rows: 10 },
    minSize: { cols: 8, rows: 3 },
    dataServices: ['BrokerFeed'],
  }),
  permissions: brokerListing({
    type: 'permissions',
    title: $localize`Permissions`,
    help:
      "The broker's users and what each may configure, write and read, with a text filter. Use it when a service " +
      'cannot connect or publish. It lists them; it changes none.',
    component: () => import('../features/broker/widgets/permissions-widget').then((m) => m.PermissionsWidget),
    defaultSize: { cols: 10, rows: 6 },
    minSize: { cols: 6, rows: 3 },
    dataServices: ['BrokerFeed'],
  }),
  projection: plain({
    type: 'bff-projection',
    title: $localize`BFF projection`,
    help:
      "The BFF's read-model projection: whether its queue has drained, a Repair that sends what the BFF inbox has " +
      'not recorded, and a Rebuild that stops the BFF, deletes its order projection, replays the outbox and ' +
      'restarts it. Use Repair first when the BFF shows stale data; Rebuild is destructive and asks for a typed ' +
      "confirmation. The job's output shows in the widget itself.",
    component: () => import('../features/broker/widgets/bff-projection-widget').then((m) => m.BffProjectionWidget),
    defaultSize: { cols: 10, rows: 12 },
    minSize: { cols: 6, rows: 5 },
    dataServices: ['BrokerFeed'],
  }),
  deadLetters: {
    type: 'dead-letters',
    title: $localize`Dead-letter workbench`,
    help:
      'Inspects a parked (dead-letter) queue, reading its first messages, and replays or discards them one message ' +
      'at a time. A dry run comes first, and the real run asks for a typed word and writes a record file. Use it to ' +
      'recover from a poison message: replay sends the message back to be handled again, and discard drops it.',
    component: () => import('../features/broker/widgets/dead-letter-widget').then((m) => m.DeadLetterWidget),
    defaultSize: { cols: 24, rows: 12 },
    minSize: { cols: 8, rows: 4 },
    config: (stored) => ({
      queue: typeof stored['queue'] === 'string' && stored['queue'] !== '' ? stored['queue'] : null,
    }),
    titleOf: (config) =>
      typeof config['queue'] === 'string'
        ? $localize`Dead letters (${config['queue']}:queue:)`
        : $localize`Dead-letter workbench`,
    presets: [{ label: $localize`Opened from a Queues widget`, config: { queue: null } }],
    fields: [{ key: 'queue', label: $localize`Queue`, kind: 'text' }],
    dataServices: ['BrokerFeed'],
  } satisfies WidgetType,
} as const;

/** The Platform map's widget (§5.2): the table of services, routes and queues, in `MapFeed`; its auto-refresh is opt-in (§4.3). */
export const MAP: WidgetType = {
  type: 'platform-map',
  title: $localize`Platform map`,
  help:
    'A table of the platform: each service with its URL, the gateway routes in front of it and its policy, and its ' +
    'queues with what consumes them. Use it to see what talks to what. It shows structure, not live traffic.',
  component: () => import('../features/map/widgets/map-widget').then((m) => m.MapWidget),
  defaultSize: { cols: 24, rows: 22 },
  minSize: { cols: 8, rows: 5 },
  config: (stored) => ({ autoRefresh: stored['autoRefresh'] === true }),
  titleOf: () => $localize`Platform map`,
  presets: [{ label: $localize`Platform map`, config: { autoRefresh: false } }],
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
  title: $localize`Platform diagram`,
  help:
    'The platform drawn as a graph of services and queues, coloured by state, with nodes that can be dragged. A ' +
    'click on a service points a Logs widget at it, and a click on a queue points a Queues widget at it or opens ' +
    'the dead-letter workbench. It is not on a built-in page; add it from Add widget.',
  component: () => import('./widgets/topology/topology-widget').then((m) => m.TopologyWidget),
  defaultSize: { cols: 24, rows: 26 },
  minSize: { cols: 10, rows: 10 },
  config: (stored) => ({ autoRefresh: stored['autoRefresh'] !== false, positions: positionsOf(stored['positions']) }),
  titleOf: () => $localize`Platform diagram`,
  presets: [{ label: $localize`Platform diagram`, config: { autoRefresh: true, positions: {} } }],
  fields: [AUTO_REFRESH],
  dataServices: ['MapFeed', 'StackFeed', 'BrokerFeed'],
};

/** What the platform sent (§5.2): the mail sink's messages, filtered by a correlation id the widget starts with. */
export const MAIL: WidgetType = {
  type: 'mail',
  title: $localize`Mail`,
  help:
    'The mail Notifications sent, as the local mail sink caught it: the list, each message, and a filter by ' +
    "correlation id. Use it to see an order's mail. It sends nothing.",
  component: () => import('../features/mail/widgets/mail-widget').then((m) => m.MailWidget),
  defaultSize: { cols: 24, rows: 18 },
  minSize: { cols: 8, rows: 5 },
  config: (stored) => ({ correlationId: typeof stored['correlationId'] === 'string' ? stored['correlationId'] : '' }),
  titleOf: (config) =>
    typeof config['correlationId'] === 'string' && config['correlationId'] !== ''
      ? $localize`Mail (${config['correlationId']}:id:)`
      : $localize`Mail`,
  presets: [{ label: $localize`Every message`, config: { correlationId: '' } }],
  fields: [{ key: 'correlationId', label: $localize`Correlation id`, kind: 'text' }],
  dataServices: [],
};

/** An erasure request raised and watched (§5.2); the request is held by `ErasureRun`, the subject being typed is session state. */
export const ERASURE: WidgetType = plain({
  type: 'erasure',
  title: $localize`Erasure`,
  help:
    "Raises a privacy erasure request for a subject id and follows each personal-data holder's answer to it. Use it " +
    'to check that every holder erased the subject. Raising a request changes data and has no undo; it does not read ' +
    'the customer data itself.',
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
    title: $localize`API request`,
    help:
      "Sends a request to the platform through the gateway as a realm user, from each service's OpenAPI operations. " +
      'Pick an operation, an identity and a body, and see the status and the response exactly as the gateway ' +
      'returned them. This is how to call the platform by hand.',
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
    titleOf: () => $localize`API request`,
    presets: [{ label: $localize`API request`, config: { operation: null, identity: null } }],
    fields: [
      { key: 'operation', label: $localize`Operation id`, kind: 'text' },
      { key: 'identity', label: $localize`Identity: anonymous, user:<name> or custom:<name>`, kind: 'text' },
    ],
    dataServices: ['ApiHistory'],
  } satisfies WidgetType,
  history: plain({
    type: 'api-history',
    title: $localize`API history`,
    help:
      'The requests this tab sent from the API request widget, newest first, which can be restored into an API ' +
      'request widget on the page. It keeps them for the tab only. It sends nothing.',
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
  title: $localize`:widget title|The widget's name:Trace`,
  help:
    "Follows one request across the services by its correlation id: Tempo's spans, the log lines and the events on " +
    'one timeline. Start from a correlation id from a response, a log line or a mail; it can follow the last Trace ' +
    'link. It does not list traces.',
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
      ? $localize`Trace (${config['correlationId']}:id:)`
      : $localize`:widget title|The widget's name:Trace`,
  presets: [
    {
      label: $localize`:widget title|The widget's name:Trace`,
      config: { correlationId: '', window: '15m', follow: false },
    },
    { label: $localize`Follows the last Trace link`, config: { correlationId: '', window: '15m', follow: true } },
  ],
  fields: [
    {
      key: 'correlationId',
      label: $localize`Correlation id, traced when the widget appears or this changes`,
      kind: 'text',
    },
    { key: 'window', label: $localize`Window: 15m, 1h, 6h or 24h`, kind: 'text' },
    { key: 'follow', label: $localize`Follow the last Trace link`, kind: 'flag' },
  ],
  dataServices: ['TraceHistory', 'TraceFollow'],
};

/**
 * Where one request went (§8.2): a Mermaid sequence diagram of a correlation id's Trace timeline. Config as the Trace
 * widget's: the id and window it starts with, and `follow`. It is drawn when asked and never polled.
 */
export const SEQUENCE: WidgetType = {
  type: 'sequence',
  title: $localize`Request sequence`,
  help:
    'The same timeline as the Trace widget drawn as a sequence diagram: where one request went, service to service. ' +
    'It is drawn when asked, not polled, and can follow the last Trace link. Use it to see the shape of a ' +
    'request; Trace has the detail.',
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
      ? $localize`Request sequence (${config['correlationId']}:id:)`
      : $localize`Request sequence`,
  presets: [
    { label: $localize`Request sequence`, config: { correlationId: '', window: '15m', follow: false } },
    { label: $localize`Follows the last Trace link`, config: { correlationId: '', window: '15m', follow: true } },
  ],
  fields: [
    {
      key: 'correlationId',
      label: $localize`Correlation id, drawn when the widget appears or this changes`,
      kind: 'text',
    },
    { key: 'window', label: $localize`Window: 15m, 1h, 6h or 24h`, kind: 'text' },
    { key: 'follow', label: $localize`Follow the last Trace link`, kind: 'flag' },
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
  title: $localize`Runbooks`,
  help:
    "The backend's runbooks, read from its clone, each with the alerts it covers and, for some, a script that " +
    'causes some of them, with an Undo that restores the stack. Use it during an incident or a game day. A costly ' +
    'cause asks for a second press or a typed word first.',
  component: () => import('../features/runbooks/widgets/runbooks-widget').then((m) => m.RunbooksWidget),
  defaultSize: { cols: 24, rows: 26 },
  minSize: { cols: 10, rows: 8 },
  config: (stored) => ({
    runbook: typeof stored['runbook'] === 'string' && stored['runbook'] !== '' ? stored['runbook'] : null,
  }),
  titleOf: (config) =>
    typeof config['runbook'] === 'string' ? $localize`Runbooks (${config['runbook']}:runbook:)` : $localize`Runbooks`,
  presets: [{ label: $localize`Runbooks`, config: { runbook: null } }],
  fields: [{ key: 'runbook', label: $localize`Runbook to open on`, kind: 'text' }],
  dataServices: [],
};

/** What the operator did this session (§5.2): the host's scrubbed transcript, read from `TranscriptFeed`. */
export const TRANSCRIPT: WidgetType = plain({
  type: 'transcript',
  title: $localize`Transcript`,
  help:
    'Each command and request the console ran, except polls, as the line that would repeat it: docker compose, npm ' +
    "and a curl for each platform request, with the assistant's requests marked and secrets scrubbed. It is kept in " +
    'memory until the host stops. Use it to see or copy what was done; it changes nothing.',
  component: () => import('../features/transcript/widgets/transcript-widget').then((m) => m.TranscriptWidget),
  defaultSize: { cols: 24, rows: 20 },
  minSize: { cols: 8, rows: 5 },
  dataServices: ['TranscriptFeed'],
});

/** The Logs page's widget (§5.2), a view of the one follow `LogFollowService` in features/logs runs. */
export const LOGS: WidgetType = listing('services', $localize`All services`, {
  type: 'logs',
  title: $localize`Logs`,
  help:
    "Follows the services' log output live, as docker compose logs prints it, for the services chosen, with a text " +
    'filter and a correlation-id filter. Use it to watch what the platform is doing now. To follow one request ' +
    'across services, use Trace with its correlation id instead.',
  component: () => import('../features/logs/widgets/logs-widget').then((m) => m.LogsWidget),
  defaultSize: { cols: 24, rows: 18 },
  minSize: { cols: 8, rows: 6 },
  dataServices: ['LogFollowService'],
});

/** Every screen's output pane, as a widget of its own (§5.2): a job by id, or the job its page started last. */
export const JOB_OUTPUT: WidgetType = {
  type: 'job-output',
  title: $localize`Job output`,
  help:
    'The output of a job the console started, live: by job id, or the job its page started last. Use it to watch a ' +
    'command that is running or read how one ended. It starts nothing.',
  component: () => import('./job-output-widget').then((m) => m.JobOutputWidget),
  defaultSize: { cols: 12, rows: 7 },
  minSize: { cols: 4, rows: 3 },
  config: (stored) => ({ jobId: typeof stored['jobId'] === 'string' ? stored['jobId'] : null }),
  titleOf: (config) =>
    typeof config['jobId'] === 'string' ? $localize`Job output (${config['jobId']}:job:)` : $localize`Job output`,
  presets: [{ label: $localize`The job this page started last`, config: { jobId: null } }],
  fields: [{ key: 'jobId', label: $localize`Job id`, kind: 'text' }],
  dataServices: ['PageJobs'],
};

/** A note or a diagram (§8.3): Markdown whose mermaid fences are drawn, written in place while editing the layout. */
export const NOTE: WidgetType = {
  type: 'note',
  title: $localize`Note`,
  help:
    'A note or a diagram: Markdown whose mermaid blocks are drawn, written in place while the layout is being ' +
    'edited. Use it for your own reminders and sketches. It reads no platform data.',
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
    return $localize`Note`;
  },
  presets: [
    { label: $localize`Empty note`, config: { source: '' } },
    { label: $localize`Note with a diagram`, config: { source: BLANK_NOTE } },
    { label: $localize`Architecture diagram`, config: { source: ARCHITECTURE_NOTE } },
    { label: $localize`Work order of the tabs`, config: { source: TAB_ORDER_NOTE } },
  ],
  configurable: false,
  dataServices: [],
};

/**
 * The assistant as a widget (AI assistant design §6.4): a view of the tab's one conversation, the same as the side panel,
 * with no built-in page. Off, a saved instance renders a notice. It reads and keeps nothing of its own.
 */
export const ASSISTANT: WidgetType = plain({
  type: 'assistant',
  title: $localize`Assistant`,
  help:
    "A view of this tab's one conversation with the assistant, the same as the side panel, so a page can hold it " +
    'beside what it asks about. Ask what a screen or widget is for, or where to find something. With the ' +
    "assistant switched off in the console's configuration it shows a notice instead; it never changes the platform.",
  component: () => import('../assistant/assistant-widget').then((m) => m.AssistantWidget),
  defaultSize: { cols: 10, rows: 20 },
  minSize: { cols: 6, rows: 8 },
  dataServices: [],
});

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
  ASSISTANT,
];

export function widgetType(type: string): WidgetType | undefined {
  return WIDGETS.find((w) => w.type === type);
}
