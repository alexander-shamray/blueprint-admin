import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HostClient } from './host-client';

describe('HostClient', () => {
  let client: HostClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    client = TestBed.inject(HostClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the stack', () => {
    let seen: unknown;
    client.stack().subscribe((s) => (seen = s));

    const req = http.expectOne('/api/stack');
    expect(req.request.method).toBe('GET');
    req.flush({ backend: { reachable: true, error: null, services: [] }, reachability: [] });

    expect(seen).toEqual({ backend: { reachable: true, error: null, services: [] }, reachability: [] });
  });

  it('sends the typed confirmation with a wipe', () => {
    client.backendDown(true, 'down -v').subscribe();

    const req = http.expectOne('/api/stack/backend/down');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ wipeVolumes: true, confirm: 'down -v' });
    req.flush({ id: 'j1', commandLine: 'docker compose down -v', state: 'Running', exitCode: null, startedAt: '' });
  });

  it('follows logs for the named services', () => {
    client.followLogs(['gateway']).subscribe();

    const req = http.expectOne('/api/logs/follow');
    expect(req.request.body).toEqual({ services: ['gateway'] });
    req.flush({ id: 'j2', commandLine: 'docker compose logs -f', state: 'Running', exitCode: null, startedAt: '' });
  });
});
