export type JobState = 'Running' | 'Exited';

export interface JobSummary {
  id: string;
  commandLine: string;
  state: JobState;
  exitCode: number | null;
  startedAt: string;
}

export interface OutputLine {
  sequence: number;
  at: string;
  stream: 'Stdout' | 'Stderr';
  text: string;
}

export interface JobView {
  summary: JobSummary;
  lines: OutputLine[];
}

export interface ServiceStatus {
  service: string;
  state: string;
  health: string | null;
  exitCode: number | null;
  publishedPorts: number[];
}

export interface ComposeStatus {
  reachable: boolean;
  error: string | null;
  services: ServiceStatus[];
}

export interface Reachability {
  name: string;
  url: string;
  up: boolean;
  status: number | null;
}

/** The reference client's last `npm start`, its last device build, and whether its clone has node_modules (spec §5.4). */
export interface FrontendStatus {
  job: JobSummary | null;
  installed: boolean;
  deviceJob: JobSummary | null;
}

/**
 * A Scenario's hold on the stack (spec §5.3, Scenario service control): one change the host made and will undo, by a
 * Restore, by its deadline, or at the next host start from its journal. `restoreError` is set when an undo failed and the
 * lease is still held.
 */
export interface LeaseView {
  id: string;
  changes: string[];
  title: string;
  services: string[];
  label: string | null;
  since: string;
  expiresAt: string;
  applyJob: string | null;
  restoreError: string | null;
}

/** A change a lease may be asked for, as the host's `ScenarioControl.Table` names it. */
export interface LeaseChange {
  id: string;
  title: string;
  service: string;
  description: string;
  maxHoldSeconds: number;
}

/** One line Ordering logged when it flagged an order for review: `Order <id> flagged for review: <reason>.` (run-locally.md). */
export interface ReviewFlag {
  at: string;
  reason: string;
  line: string;
}

/**
 * What Ordering logged about one order being flagged for review, read from Loki by the host (spec §5.3). `reachable` is false
 * when Loki could not be asked, and `error` says why; an empty `flags` with `reachable` true means nothing was logged yet.
 */
export interface ReviewFlagView {
  orderId: string;
  window: string;
  reachable: boolean;
  error: string | null;
  flags: ReviewFlag[];
}

export interface LeaseState {
  lease: LeaseView | null;
  changes: LeaseChange[];
}

export interface LeaseRequest {
  changes: string[];
  holdSeconds: number;
  label?: string;
}

export interface StackView {
  backend: ComposeStatus;
  frontend: FrontendStatus;
  reachability: Reachability[];
  lease: LeaseView | null;
}

export interface ConfigView {
  backendDir: string;
  frontendDir: string;
  composeFile: string;
  fakePlatform: boolean;
  urls: {
    gateway: string;
    catalog: string;
    ordering: string;
    bff: string;
    inventory: string;
    payments: string;
    paymentSimulator: string;
    carrierSimulator: string;
    keycloak: string;
    grafana: string;
    client: string;
  };
}

/** A realm user the host can mint a token for; the host keeps the password (spec §5.6). */
export interface RealmUserView {
  username: string;
}

/** No username is anonymous; a username alone is a configured realm user; both is a custom identity. */
export interface Identity {
  username: string | null;
  password: string | null;
}

export interface TokenView {
  username: string;
  accessToken: string;
  expiresAt: string;
  claims: Record<string, unknown>;
}

/**
 * The host's `TokenClockView`: whether it holds a token for the identity, when that token expires and when it
 * stops being reused (the host's `TokenService.ReuseMargin` decides that, not the page), and what it grants.
 */
export interface TokenClockView {
  username: string | null;
  held: boolean;
  expiresAt: string | null;
  renewsAt: string | null;
  permissions: string[];
}

export interface ApiParameter {
  name: string;
  required: boolean;
  type: string | null;
}

/** One call on the API screen (spec §5.7). `url` is absolute and may hold `{name}` path placeholders. */
export interface ApiOperation {
  id: string;
  source: string;
  name: string;
  method: string;
  url: string;
  pathParameters: ApiParameter[];
  queryParameters: ApiParameter[];
  exampleBody: string | null;
  hasCommandId: boolean;
  edgePolicy: string;
  available: boolean;
}

/**
 * One document against the baseline the host keeps for its service (the host's `OpenApiChanges`).
 * `baselineTaken` is the first sight of the service; `error` is a baseline that could not be read or written.
 */
export interface OpenApiChanges {
  baselineAt: string | null;
  baselineTaken: boolean;
  operationsAdded: string[];
  operationsRemoved: string[];
  statusesAdded: string[];
  statusesRemoved: string[];
  fieldsAdded: string[];
  fieldsRemoved: string[];
  error: string | null;
}

export interface ApiSource {
  name: string;
  documentUrl: string;
  available: boolean;
  error: string | null;
  /** Null when no document was read on the last load. */
  changes: OpenApiChanges | null;
}

export interface ApiCatalogView {
  sources: ApiSource[];
  operations: ApiOperation[];
}

/** The pre-signed URL and the headers the BFF signed with it; the host sends its own sample image there and nowhere else. */
export interface ProductImageUploadRequest {
  uploadUrl: string;
  headers: Record<string, string>;
}

export interface ProxyRequest {
  method: string;
  url: string;
  headers: Record<string, string>;
  body: string | null;
  identity: Identity | null;
  correlationId: string | null;
}

/** `responded` is the platform's answer untouched; the other two mean the request got no answer from it (spec §9). */
export type ProxyResult =
  | {
      outcome: 'responded';
      status: number;
      headers: Record<string, string[]>;
      body: string;
      bodyTruncated: boolean;
      /** Why the body read broke off after the response began; null when it was read to its end. */
      bodyError: string | null;
      elapsedMs: number;
      correlationId: string;
    }
  // `sent` is false when the identity's token could not be minted, which happens before anything
  // leaves the console: the platform has never seen this correlation id, so there is no trace of it.
  | { outcome: 'unreached'; error: string; elapsedMs: number; correlationId: string; sent: boolean }
  | { outcome: 'tokenRejected'; status: number; body: string; correlationId: string };

/** A queue and its depth; `messages` is ready plus unacknowledged, as rabbitmqctl reports it (spec §5.5). */
export interface BrokerQueue {
  name: string;
  messages: number | null;
  /** What consumes it, from the host's `PlatformQueues.Table`; null for a queue that table does not name. */
  consumer: string | null;
  /** The kind of parked queue this is, from the host's `ParkedQueues`; null for a queue that is not one. */
  parked: 'error' | 'skipped' | null;
  /** The runbook that owns a parked queue, a link the browser follows; null for a queue that is not parked. */
  runbook: string | null;
}

/**
 * What the Broker screen says before either projection button is pressed. The host owns the words: each limit restates
 * a sentence of the backend's `tools/bff-replay` README that the drift gate holds it to, and `rebuildConfirm` is what a
 * person types before a rebuild deletes the projection.
 */
export interface ProjectionPlan {
  queue: string;
  rebuildConfirm: string;
  limits: string[];
}

/**
 * What the Broker screen says before a dead letter is read or moved: what a person types before the real run, how
 * many messages one read takes and puts back, and where each real run's record file is written.
 */
export interface DeadLetterPlan {
  executeConfirm: string;
  inspectLimit: number;
  recordDir: string;
}

/** One parked message as the backend's `tools/dead-letters` describes it (its README, "The JSON contract"). */
export interface DeadLetterMessage {
  messageId: string | null;
  correlationId: string | null;
  messageType: string | null;
  faultMessage: string | null;
  faultExceptionType: string | null;
  inputAddress: string | null;
  reason: string | null;
  redelivered: boolean;
  /** Null when the body is not UTF-8. */
  body: string | null;
}

/** A parked queue's messages, read and put back; `error` says why there are none when the tool or broker did not answer. */
export interface DeadLetterInspection {
  queue: string;
  endpoint: string | null;
  kind: string | null;
  messages: DeadLetterMessage[];
  error: string | null;
}

export type DeadLetterVerb = 'replay' | 'discard';

/** Whether a queue is drained: declared and empty. The queue is `PlatformQueues.Projection`, which owns what it feeds. */
export interface ProjectionDrain {
  queue: string;
  found: boolean;
  messages: number | null;
  drained: boolean;
}

export interface QueuesView {
  reachable: boolean;
  error: string | null;
  queues: BrokerQueue[];
  projection: ProjectionDrain;
}

/** A holder Privacy asks to erase a subject, and the depth of its `*-privacy` queue; null when the broker did not say. */
export interface ErasureHolder {
  name: string;
  queue: string;
  pending: number | null;
}

/** The host's `HoldersView`: the holders in the backend's order, `privacy-completions`' depth and the overdue runbook. */
export interface HoldersView {
  reachable: boolean;
  error: string | null;
  holders: ErasureHolder[];
  completionsPending: number | null;
  runbookUrl: string;
}

/** A mailbox as the mail sink parsed it; `name` is empty when the message gave none. */
export interface Mailbox {
  name: string;
  address: string;
}

/** One message in the host's `MailListView`; `correlationId` is null when the message carries none the backend would adopt. */
export interface MailSummary {
  id: string;
  from: Mailbox;
  to: Mailbox[];
  subject: string;
  created: string;
  snippet: string;
  read: boolean;
  correlationId: string | null;
}

/** The host's `MailListView`: the newest messages first, what the sink holds in all, and the sink's own UI. */
export interface MailListView {
  reachable: boolean;
  error: string | null;
  total: number;
  messages: MailSummary[];
  sinkUrl: string;
}

/** One message opened, with the parts the sink split it into; either may be empty. */
export interface MailMessageView {
  id: string;
  from: Mailbox;
  to: Mailbox[];
  cc: Mailbox[];
  subject: string;
  date: string;
  text: string;
  html: string;
  correlationId: string | null;
}

/** The host's `MailMessageRead`: the message, or why there is none. */
export interface MailMessageRead {
  reachable: boolean;
  error: string | null;
  message: MailMessageView | null;
}

/** One holder's answer; `counted` is false for a name outside the set the request was raised with. */
export interface HolderAnswer {
  responder: string;
  count: number;
  counted: boolean;
  receivedAt: string;
}

/** Privacy's `ErasureRequestView`, as the gateway answers `GET /api/v1/privacy/erasure-requests/{id}`; never the subject. */
export interface ErasureRequestView {
  requestId: string;
  status: 'Open' | 'Overdue' | 'Closed';
  raisedAt: string;
  dueAt: string;
  closedAt: string | null;
  reissues: number;
  responders: string[];
  missing: string[];
  answers: HolderAnswer[];
}

export interface BrokerExchange {
  name: string;
  type: string;
}

export interface ExchangesView {
  reachable: boolean;
  error: string | null;
  exchanges: BrokerExchange[];
}

export interface BrokerPermission {
  user: string;
  configure: string;
  write: string;
  read: string;
}

export interface PermissionsView {
  reachable: boolean;
  error: string | null;
  permissions: BrokerPermission[];
}

/**
 * What one row of a timeline is (spec §5.9). `Queued` is the terminal marker: the backend's outbox
 * carries no trace context, so the publish runs in a trace this correlation id cannot reach, and the
 * event's summary says so in words.
 */
export type TraceEventKind = 'HttpIn' | 'Log' | 'Span' | 'Outbox' | 'Publish' | 'Consume' | 'Saga' | 'Queued' | 'Error';

/** One row of a timeline. `source` is `loki`, `tempo` or `broker`; `link` opens Grafana Explore. */
export interface TraceEvent {
  at: string;
  source: string;
  service: string;
  kind: TraceEventKind;
  summary: string;
  traceId: string | null;
  link: string | null;
}

/** One correlation id's timeline. A Grafana that does not answer is a state, as `QueuesView` does it. */
export interface TraceView {
  correlationId: string;
  window: string;
  reachable: boolean;
  error: string | null;
  traceIds: string[];
  tracesTruncated: boolean;
  /** A partial failure: the timeline is real but some traces would not come back from Tempo. */
  warning: string | null;
  events: TraceEvent[];
}

/**
 * One service's golden signals, a value per query panel of the backend's dashboard: rates per second,
 * the 5xx fraction and durations in seconds. Null where unknown; `TelemetryHealthService` decides which absences are zeros.
 */
export interface ServiceSignals {
  service: string;
  requestRate: number | null;
  errorRatio: number | null;
  latencyP99Seconds: number | null;
  domainRefusalRate: number | null;
  unauthorisedRate: number | null;
  commandP95Seconds: number | null;
  queryP95Seconds: number | null;
}

/** The Stack screen's golden-signal strip. A Grafana that does not answer is a state, as `TraceView` does it. */
export interface TelemetryHealthView {
  reachable: boolean;
  error: string | null;
  services: ServiceSignals[];
}

/**
 * One loaded alerting rule as Prometheus reports it: `state` is `inactive`, `pending` or `firing`, `health` is whether
 * its last evaluation worked, and `alerts` counts the instances alight. `severity` and `summary` are null where the rule has none.
 */
export interface AlertRule {
  name: string;
  group: string;
  state: string;
  health: string;
  severity: string | null;
  summary: string | null;
  alerts: number;
}

/** A rule the backend wrote out and does not load, and the metrics it reads that nothing publishes yet. */
export interface WaitingAlert {
  name: string;
  instruments: string[];
}

/**
 * The Stack screen's alert strip. `loaded` means something only where `reachable`; `waiting` is always the backend's
 * list, because it does not depend on any Grafana answering.
 */
export interface AlertStripView {
  reachable: boolean;
  error: string | null;
  loaded: AlertRule[];
  waiting: WaitingAlert[];
}

/** One workstation check, as the host's `WorkstationDoctor` judged it; that class says when a row is `Unknown`. */
export interface DoctorCheck {
  name: string;
  state: 'Ok' | 'Problem' | 'Unknown';
  detail: string;
}

export interface DoctorView {
  checks: DoctorCheck[];
}

/**
 * One thing the operator did, as the host's `OperatorTranscript` kept it: a process with its directory and exit
 * code, or a request as its curl with the identity it went as and the status that came back. Already scrubbed.
 * `settled` is false while the process runs or the request awaits its answer; a settled request with no status
 * got no answer.
 */
export interface TranscriptEntry {
  sequence: number;
  at: string;
  kind: 'Process' | 'Request';
  command: string;
  workingDirectory: string | null;
  identity: string | null;
  exitCode: number | null;
  status: number | null;
  settled: boolean;
}

export interface TranscriptView {
  entries: TranscriptEntry[];
  dropped: number;
}
