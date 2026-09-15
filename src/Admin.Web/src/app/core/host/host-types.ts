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

/** The reference client's last `npm start` and whether its clone has node_modules (spec §5.4). */
export interface FrontendStatus {
  job: JobSummary | null;
  installed: boolean;
}

export interface StackView {
  backend: ComposeStatus;
  frontend: FrontendStatus;
  reachability: Reachability[];
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

export interface ApiSource {
  name: string;
  documentUrl: string;
  available: boolean;
  error: string | null;
}

export interface ApiCatalogView {
  sources: ApiSource[];
  operations: ApiOperation[];
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
  | { outcome: 'unreached'; error: string; elapsedMs: number; correlationId: string }
  | { outcome: 'tokenRejected'; status: number; body: string; correlationId: string };

/** A queue and its depth; `messages` is ready plus unacknowledged, as rabbitmqctl reports it (spec §5.5). */
export interface BrokerQueue {
  name: string;
  messages: number | null;
  isErrorQueue: boolean;
}

/** Whether a queue has caught up: declared and empty. `ordering-catalog-events` is Ordering's price projection. */
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
