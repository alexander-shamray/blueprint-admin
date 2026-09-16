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

  it('starts the frontend', () => {
    client.frontendStart().subscribe();

    const req = http.expectOne('/api/stack/frontend/start');
    expect(req.request.method).toBe('POST');
    req.flush({ id: 'fe-1', commandLine: 'npm start', state: 'Running', exitCode: null, startedAt: '' });
  });

  it('stops the frontend', () => {
    client.frontendStop().subscribe();

    const req = http.expectOne('/api/stack/frontend/stop');
    expect(req.request.method).toBe('POST');
    req.flush({ id: 'fe-1', commandLine: 'npm start', state: 'Exited', exitCode: -1, startedAt: '' });
  });

  it('lists realm users', () => {
    client.identityUsers().subscribe();

    const req = http.expectOne('/api/identity/users');
    expect(req.request.method).toBe('GET');
    req.flush([{ username: 'demo' }]);
  });

  it('mints a token for an identity', () => {
    client.token({ username: 'demo', password: null }).subscribe();

    const req = http.expectOne('/api/identity/token');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ username: 'demo', password: null });
    req.flush({ username: 'demo', accessToken: 'a.b.c', expiresAt: '', claims: {} });
  });

  it('reads and reloads the operation catalog', () => {
    client.operations().subscribe();
    const read = http.expectOne('/api/catalog/operations');
    expect(read.request.method).toBe('GET');
    read.flush({ sources: [], operations: [] });

    client.reloadOperations().subscribe();
    const reload = http.expectOne('/api/catalog/reload');
    expect(reload.request.method).toBe('POST');
    reload.flush({ sources: [], operations: [] });
  });

  it('proxies a request', () => {
    const request = {
      method: 'GET',
      url: 'http://localhost:5000/api/v1/catalog/products/',
      headers: {},
      body: null,
      identity: null,
      correlationId: null,
    };
    client.proxy(request).subscribe();

    const req = http.expectOne('/api/proxy');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush({ outcome: 'unreached', error: 'refused', elapsedMs: 1, correlationId: 'c' });
  });

  it('reads the broker queues, exchanges and permissions', () => {
    client.brokerQueues().subscribe();
    const queues = http.expectOne('/api/broker/queues');
    expect(queues.request.method).toBe('GET');
    queues.flush({ reachable: true, error: null, queues: [], projection: { queue: 'ordering-catalog-events', found: false, messages: null, drained: false } });

    client.brokerExchanges().subscribe();
    const exchanges = http.expectOne('/api/broker/exchanges');
    expect(exchanges.request.method).toBe('GET');
    exchanges.flush({ reachable: true, error: null, exchanges: [] });

    client.brokerPermissions().subscribe();
    const permissions = http.expectOne('/api/broker/permissions');
    expect(permissions.request.method).toBe('GET');
    permissions.flush({ reachable: true, error: null, permissions: [] });
  });
  it('asks for a trace by correlation id, escaping it, with the window as a query parameter', () => {
    client.trace('demo trace/0001', '2h').subscribe();
    const req = http.expectOne((r) => r.url === '/api/trace/demo%20trace%2F0001');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('window')).toBe('2h');
    req.flush({ correlationId: 'demo trace/0001', window: '2h', reachable: true, error: null, traceIds: [], tracesTruncated: false, events: [] });
  });

  it('omits the window parameter entirely when none is asked for, so the host applies its default', () => {
    client.trace('demo-trace-0001').subscribe();
    const req = http.expectOne('/api/trace/demo-trace-0001');
    expect(req.request.params.keys()).toEqual([]);
    req.flush({ correlationId: 'demo-trace-0001', window: '15m', reachable: true, error: null, traceIds: [], tracesTruncated: false, events: [] });
  });
});
