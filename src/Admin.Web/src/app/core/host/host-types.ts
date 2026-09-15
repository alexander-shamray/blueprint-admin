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
