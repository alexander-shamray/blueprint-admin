import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  ApiCatalogView,
  ConfigView,
  ExchangesView,
  Identity,
  JobSummary,
  JobView,
  PermissionsView,
  ProxyRequest,
  ProxyResult,
  QueuesView,
  RealmUserView,
  StackView,
  TokenView,
  TraceView,
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

  frontendStart(): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/frontend/start', null);
  }

  frontendStop(): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/frontend/stop', null);
  }

  followLogs(services: string[]): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/logs/follow', { services });
  }

  identityUsers(): Observable<RealmUserView[]> {
    return this.http.get<RealmUserView[]>('/api/identity/users');
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

  proxy(request: ProxyRequest): Observable<ProxyResult> {
    return this.http.post<ProxyResult>('/api/proxy', request);
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

  trace(correlationId: string, window?: string): Observable<TraceView> {
    const params = window ? new HttpParams().set('window', window) : undefined;
    return this.http.get<TraceView>(`/api/trace/${encodeURIComponent(correlationId)}`, { params });
  }

  job(id: string): Observable<JobView> {
    return this.http.get<JobView>(`/api/jobs/${encodeURIComponent(id)}`);
  }
}
