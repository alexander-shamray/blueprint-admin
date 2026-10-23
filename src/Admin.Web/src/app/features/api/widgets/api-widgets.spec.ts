import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { ApiCatalogView, ApiOperation, ProxyResult } from '../../../core/host/host-types';
import { IdentityState } from '../../../core/identity/identity-state';
import { WIDGET_CONTEXT } from '../../../workspace/widget-context';
import { WorkspaceService } from '../../../workspace/workspace-service';
import { ApiHistory, UUID } from '../api-client';
import { ApiHistoryWidget } from './api-history-widget';
import { ApiRequestWidget } from './api-request-widget';

function op(partial: Partial<ApiOperation>): ApiOperation {
  return {
    id: 'catalog:GetProducts', source: 'catalog', name: 'GetProducts', method: 'GET',
    url: 'http://localhost:5000/api/v1/catalog/products/', pathParameters: [], queryParameters: [],
    exampleBody: null, hasCommandId: false, edgePolicy: 'anonymous', available: true, ...partial,
  };
}

const catalog: ApiCatalogView = {
  sources: [{ name: 'catalog', documentUrl: 'http://localhost:5102/openapi/v1.json', available: true, error: null, changes: null }],
  operations: [op({}), op({ id: 'catalog:PublishProduct', name: 'PublishProduct', method: 'POST', exampleBody: '{"name":"Walnut desk"}' })],
};

const responded: ProxyResult = {
  outcome: 'responded', status: 200, headers: {}, body: '{}', bodyTruncated: false, bodyError: null, elapsedMs: 5, correlationId: 'corr-1',
};

describe('the API request and API history widgets', () => {
  let host: Record<string, ReturnType<typeof vi.fn>>;
  let configured: Record<string, unknown>[];

  beforeEach(() => {
    configured = [];
    host = {
      operations: vi.fn(() => of(catalog)),
      reloadOperations: vi.fn(() => of(catalog)),
      acceptBaseline: vi.fn(() => of(catalog)),
      proxy: vi.fn(() => of(responded)),
      token: vi.fn(),
      tokenClock: vi.fn((identity: { username: string }) => of({ username: identity.username, held: false, expiresAt: null, renewsAt: null, permissions: [] })),
      identityUsers: vi.fn(() => of([{ username: 'demo' }, { username: 'browser' }])),
      brokerQueues: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HostClient, useValue: host },
        { provide: UUID, useValue: () => 'fresh-uuid' },
        { provide: WIDGET_CONTEXT, useValue: { instanceId: 'w1', pageId: 'p1' } },
      ],
    });
    vi.spyOn(TestBed.inject(WorkspaceService), 'configureWidget').mockImplementation((_page, _widget, config) => {
      configured.push(config);
    });
  });

  function request(config: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(ApiRequestWidget);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    return fixture;
  }

  function history() {
    const fixture = TestBed.createComponent(ApiHistoryWidget);
    fixture.detectChanges();
    return fixture;
  }

  describe('config', () => {
    it('starts on the operation it names once the catalog has it', () => {
      const fixture = request({ operation: 'catalog:PublishProduct' });

      expect(fixture.componentInstance.selectedId()).toBe('catalog:PublishProduct');
      expect(fixture.componentInstance.method()).toBe('POST');
      expect(fixture.componentInstance.body()).toContain('Walnut desk');
      // Selecting what the config named is not a change to save.
      expect(configured).toEqual([]);
    });

    it('does not take the configured operation back after the operator chose another', () => {
      const fixture = request({ operation: 'catalog:PublishProduct' });
      fixture.componentInstance.select(catalog.operations[0]);
      fixture.componentRef.setInput('config', { operation: 'catalog:PublishProduct' });
      fixture.detectChanges();

      expect(fixture.componentInstance.selectedId()).toBe('catalog:GetProducts');
    });

    it('takes up the identity it names, and saves the operator\'s choice of identity and operation by username', () => {
      const fixture = request({ identity: 'user:browser' });
      expect(TestBed.inject(IdentityState).choice()).toEqual({ kind: 'user', username: 'browser' });
      expect(configured).toEqual([]);

      fixture.componentInstance.select(catalog.operations[1]);
      fixture.componentInstance.chooseIdentity('anonymous');

      expect(configured.at(-1)).toEqual({ operation: 'catalog:PublishProduct', identity: 'anonymous' });
    });
  });

  describe('a custom identity\'s password', () => {
    it('never reaches a saved config: only the username does, however often it is typed', () => {
      const fixture = request();
      fixture.componentInstance.chooseIdentity('custom');
      fixture.componentInstance.setCustom('alice', 's');
      fixture.componentInstance.setCustom('alice', 's3cret-password');

      expect(configured.length).toBeGreaterThan(0);
      expect(configured.at(-1)).toEqual({ operation: null, identity: 'custom:alice' });
      expect(JSON.stringify(configured)).not.toContain('s3cret');
      expect(JSON.stringify(configured).toLowerCase()).not.toContain('password');
    });

    it('is session state: a re-created widget fills it back in for the username its config names, and sends with it', () => {
      const first = request({ identity: 'custom:alice' });
      first.componentInstance.setCustom('alice', 's3cret-password');
      first.destroy();
      // The shared identity forgets it on leaving, as it always did.
      expect(TestBed.inject(IdentityState).choice()).toEqual({ kind: 'custom', username: 'alice', password: '' });

      const again = request({ identity: 'custom:alice' });
      again.componentInstance.send();

      expect(again.componentInstance.customPassword()).toBe('s3cret-password');
      expect(host['proxy'].mock.calls.at(-1)?.[0].identity).toEqual({ username: 'alice', password: 's3cret-password' });
    });

    it('is not in what the history keeps', () => {
      const fixture = request();
      fixture.componentInstance.setCustom('alice', 's3cret-password');
      fixture.componentInstance.send();

      expect(JSON.stringify(TestBed.inject(ApiHistory).entries())).not.toContain('s3cret');
    });
  });

  describe('history', () => {
    it('lists what the request widget sent, newest first, and says to add a request widget when there is none to restore into', () => {
      const requestFixture = request();
      requestFixture.componentInstance.chooseIdentity('anonymous');
      requestFixture.componentInstance.send();
      requestFixture.destroy();

      const fixture = history();

      expect(fixture.nativeElement.querySelectorAll('li').length).toBe(1);
      expect(fixture.nativeElement.querySelector('li button')).toBeNull();
      expect(fixture.nativeElement.textContent).toContain('Add an API request widget');
    });

    it('restores an entry into a request widget on the page, which takes the request back as it was sent', () => {
      const requestFixture = request();
      requestFixture.componentInstance.select(catalog.operations[1]);
      requestFixture.componentInstance.chooseIdentity('anonymous');
      requestFixture.componentInstance.send();
      requestFixture.componentInstance.select(catalog.operations[0]);

      const fixture = history();
      (fixture.nativeElement.querySelector('li button') as HTMLButtonElement).click();
      requestFixture.detectChanges();

      expect(requestFixture.componentInstance.selectedId()).toBe('catalog:PublishProduct');
      expect(requestFixture.componentInstance.method()).toBe('POST');
      expect(requestFixture.componentInstance.result()).not.toBeNull();
    });

    it('does not replay a restore into a request widget that appears afterwards', () => {
      const first = request();
      first.componentInstance.chooseIdentity('anonymous');
      first.componentInstance.send();
      const fixture = history();
      (fixture.nativeElement.querySelector('li button') as HTMLButtonElement).click();
      first.destroy();

      const later = request();

      expect(later.componentInstance.result()).toBeNull();
    });

    it('counts the request widgets showing, so a restore is offered only while there is one', () => {
      const api = TestBed.inject(ApiHistory);
      expect(api.canRestore()).toBe(false);

      const fixture = request();
      expect(api.canRestore()).toBe(true);

      fixture.destroy();
      expect(api.canRestore()).toBe(false);
    });

    it('keeps the newest fifty', () => {
      const api = TestBed.inject(ApiHistory);
      for (let i = 0; i < 55; i++) {
        api.add({ name: `call ${i}`, operationId: null, hasCommandId: false, headersText: '', urlTemplate: '', pathValues: {}, queryValues: {}, bodyTemplate: '', identity: 'anonymous', sentAs: { kind: 'anonymous' }, request: { method: 'GET', url: '', headers: {}, body: null, identity: null, correlationId: null }, result: responded });
      }

      expect(api.entries().length).toBe(50);
      expect(api.entries()[0].name).toBe('call 54');
    });
  });
});
