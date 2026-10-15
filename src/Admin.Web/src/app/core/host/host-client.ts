import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  ApiCatalogView,
  ConfigView,
  ExchangesView,
  HoldersView,
  Identity,
  JobSummary,
  DoctorView,
  JobView,
  PermissionsView,
  ProjectionPlan,
  ProductImageUploadRequest,
  ProxyRequest,
  ProxyResult,
  QueuesView,
  RealmUserView,
  StackView,
  TelemetryHealthView,
  TokenClockView,
  TokenView,
  TraceView,
  TranscriptView,
} from './host-types';

/** Every call the SPA makes; there is no other origin (spec §3, §7). */
@Injectable({ providedIn: 'root' })
export class HostClient {
  private readonly http = inject(HttpClient);

  config(): Observable<ConfigView> {
    return this.http.get<ConfigView>('/api/config');
  }

  stack(): Observable<StackView> {
    return this.http.get<StackView>('/api/stack');
  }

  backendUp(): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/backend/up', null);
  }

  backendDown(wipeVolumes: boolean, confirm?: string): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/backend/down', { wipeVolumes, confirm });
  }

  /** Down with volumes, up, and the readiness wait, as one job. Takes the same typed confirmation as a wipe. */
  backendReset(confirm: string): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/backend/reset', { confirm });
  }

  frontendStart(): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/frontend/start', null);
  }

  frontendStop(): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/frontend/stop', null);
  }

  followLogs(services: string[]): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/logs/follow', { services });
  }

  /** Ends the follow job `jobId`; the host leaves a job that is no longer its current follow alone (spec §5.10). */
  stopFollow(jobId: string): Observable<void> {
    return this.http.post<void>(`/api/logs/follow/${encodeURIComponent(jobId)}/stop`, null);
  }

  identityUsers(): Observable<RealmUserView[]> {
    return this.http.get<RealmUserView[]>('/api/identity/users');
  }

  /** Reads the token the host holds for the identity without minting one, and never the token itself. */
  tokenClock(identity: Identity): Observable<TokenClockView> {
    return this.http.post<TokenClockView>('/api/identity/clock', identity);
  }

  token(identity: Identity): Observable<TokenView> {
    return this.http.post<TokenView>('/api/identity/token', identity);
  }

  operations(): Observable<ApiCatalogView> {
    return this.http.get<ApiCatalogView>('/api/catalog/operations');
  }

  reloadOperations(): Observable<ApiCatalogView> {
    return this.http.post<ApiCatalogView>('/api/catalog/reload', null);
  }

  /** Makes the service's last fetched document the baseline its changes are read against; answers the reloaded catalog. */
  acceptBaseline(service: string): Observable<ApiCatalogView> {
    return this.http.post<ApiCatalogView>(`/api/catalog/baseline/${encodeURIComponent(service)}`, null);
  }

  proxy(request: ProxyRequest): Observable<ProxyResult> {
    return this.http.post<ProxyResult>('/api/proxy', request);
  }

  /** The image's bytes to the pre-signed URL the BFF issued: the one call the proxy cannot carry, because its body is text. */
  uploadProductImage(request: ProductImageUploadRequest): Observable<ProxyResult> {
    return this.http.post<ProxyResult>('/api/product-images/upload', request);
  }

  /** The holders an erasure request waits on, each with its queue's depth. The request itself goes through `proxy`. */
  erasureHolders(): Observable<HoldersView> {
    return this.http.get<HoldersView>('/api/erasure/holders');
  }

  brokerQueues(): Observable<QueuesView> {
    return this.http.get<QueuesView>('/api/broker/queues');
  }

  brokerExchanges(): Observable<ExchangesView> {
    return this.http.get<ExchangesView>('/api/broker/exchanges');
  }

  brokerPermissions(): Observable<PermissionsView> {
    return this.http.get<PermissionsView>('/api/broker/permissions');
  }

  /** What to say before either projection button is pressed: the queue, the typed confirmation, the tool's limits. */
  projectionPlan(): Observable<ProjectionPlan> {
    return this.http.get<ProjectionPlan>('/api/broker/projection');
  }

  /** The BFF projection repaired: the tool with no argument, nothing deleted. */
  projectionRepair(): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/broker/projection/repair', null);
  }

  /** The BFF projection rebuilt: stop the BFF, the tool with `--reset`, start it again. Takes the typed confirmation. */
  projectionRebuild(confirm: string): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/broker/projection/rebuild', { confirm });
  }

  trace(correlationId: string, window?: string): Observable<TraceView> {
    const params = window ? new HttpParams().set('window', window) : undefined;
    return this.http.get<TraceView>(`/api/trace/${encodeURIComponent(correlationId)}`, { params });
  }

  telemetryHealth(): Observable<TelemetryHealthView> {
    return this.http.get<TelemetryHealthView>('/api/telemetry/health');
  }

  doctor(): Observable<DoctorView> {
    return this.http.get<DoctorView>('/api/stack/doctor');
  }

  /** What the operator did this session; the host keeps it in memory and scrubs it (spec §5.11). */
  transcript(): Observable<TranscriptView> {
    return this.http.get<TranscriptView>('/api/transcript');
  }

  /** The same transcript as a bash script, rendered and scrubbed by the host rather than here. */
  transcriptScript(): Observable<string> {
    return this.http.get('/api/transcript/script', { responseType: 'text' });
  }

  job(id: string): Observable<JobView> {
    return this.http.get<JobView>(`/api/jobs/${encodeURIComponent(id)}`);
  }
}
