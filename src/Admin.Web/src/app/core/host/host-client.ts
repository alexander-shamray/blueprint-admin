import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigView, JobSummary, JobView, StackView } from './host-types';

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

  followLogs(services: string[]): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/logs/follow', { services });
  }

  job(id: string): Observable<JobView> {
    return this.http.get<JobView>(`/api/jobs/${encodeURIComponent(id)}`);
  }
}
