import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ApiCatalogView, ApiOperation, ProxyResult } from '../../core/host/host-types';
import { ApiPage, UUID } from './api-page';

function op(partial: Partial<ApiOperation>): ApiOperation {
  return {
    id: 'catalog:GetProducts', source: 'catalog', name: 'GetProducts', method: 'GET',
    url: 'http://localhost:5000/api/v1/catalog/products/', pathParameters: [], queryParameters: [],
    exampleBody: null, hasCommandId: false, edgePolicy: 'anonymous', available: true, ...partial,
  };
}

const catalog: ApiCatalogView = {
  sources: [
    { name: 'catalog', documentUrl: 'http://localhost:5102/openapi/v1.json', available: true, error: null },
    { name: 'ordering', documentUrl: 'http://localhost:5101/openapi/v1.json', available: false, error: 'answered 503.' },
  ],
  operations: [
    op({}),
    op({
      id: 'catalog:PublishProduct', name: 'PublishProduct', method: 'POST', edgePolicy: 'authenticated',
      exampleBody: '{"commandId":"00000000-0000-0000-0000-000000000000","name":"Walnut desk"}', hasCommandId: true,
    }),
    op({
      id: 'ordering:CancelOrder', source: 'ordering', name: 'CancelOrder', method: 'POST', available: false,
      url: 'http://localhost:5000/api/v1/orders/{id}/cancel', pathParameters: [{ name: 'id', required: true, type: 'string' }],
    }),
  ],
};

const responded: ProxyResult = {
  outcome: 'responded', status: 200, headers: { 'Content-Type': ['application/json'] },
  body: '{"items":[]}', bodyTruncated: false, bodyError: null, elapsedMs: 12, correlationId: 'corr-1',
};

describe('ApiPage', () => {
  let host: {
    operations: ReturnType<typeof vi.fn>;
    reloadOperations: ReturnType<typeof vi.fn>;
    proxy: ReturnType<typeof vi.fn>;
    token: ReturnType<typeof vi.fn>;
    identityUsers: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    host = {
      operations: vi.fn(() => of(catalog)),
      reloadOperations: vi.fn(() => of(catalog)),
      proxy: vi.fn(() => of(responded)),
      token: vi.fn(() => of({ username: 'demo', accessToken: 'a.b.c', expiresAt: '2026-09-15T08:05:00Z', claims: { permission: ['catalog:write'] } })),
      identityUsers: vi.fn(() => of([{ username: 'demo' }, { username: 'browser' }])),
    };
    TestBed.configureTestingModule({
      imports: [ApiPage],
      providers: [{ provide: HostClient, useValue: host }, { provide: UUID, useValue: () => 'fresh-uuid' }],
    });
  });

  function render() {
    const fixture = TestBed.createComponent(ApiPage);
    fixture.detectChanges();
    return fixture;
  }

  function click(fixture: ReturnType<typeof render>, name: string): void {
    const button = (Array.from(fixture.nativeElement.querySelectorAll('button')) as HTMLButtonElement[]).find((b) => b.textContent?.trim().startsWith(name));
    button!.click();
    fixture.detectChanges();
  }

  it('lists operations by source and shows an unavailable source with its error', () => {
    const fixture = render();
    const text = fixture.nativeElement.textContent as string;

    expect(Array.from(fixture.nativeElement.querySelectorAll('button.op')).map((b) => (b as HTMLElement).textContent?.trim()))
      .toEqual([expect.stringContaining('GetProducts'), expect.stringContaining('PublishProduct'), expect.stringContaining('CancelOrder')]);
    expect(text).toContain('answered 503.');
    expect(fixture.nativeElement.querySelector('button.op.unavailable')).not.toBeNull();
  });

  it('selecting an operation fills the method, url and example body', () => {
    const fixture = render();
    click(fixture, 'POST PublishProduct');

    const page = fixture.componentInstance;
    expect(page.method()).toBe('POST');
    expect(page.url()).toBe('http://localhost:5000/api/v1/catalog/products/');
    expect(page.body()).toContain('Walnut desk');
  });

  it('sends as the chosen identity with a fresh commandId and shows the response', () => {
    const fixture = render();
    click(fixture, 'POST PublishProduct');
    fixture.componentInstance.correlationId.set('my-corr');
    click(fixture, 'Send');

    expect(host.proxy).toHaveBeenCalledWith({
      method: 'POST',
      url: 'http://localhost:5000/api/v1/catalog/products/',
      headers: {},
      body: '{\n  "commandId": "fresh-uuid",\n  "name": "Walnut desk"\n}',
      identity: { username: 'demo', password: null },
      correlationId: 'my-corr',
    });
    const response = fixture.nativeElement.querySelector('.response') as HTMLElement;
    expect(response.querySelector('.status')?.textContent).toContain('200');
    expect(response.querySelector('.correlation')?.textContent).toContain('corr-1');
    expect(response.querySelector('pre.body')?.textContent).toContain('"items": []');
  });

  it('fills path parameters into the url', () => {
    const fixture = render();
    click(fixture, 'POST CancelOrder');
    fixture.componentInstance.setPathValue('id', 'abc');
    click(fixture, 'Send');

    expect(host.proxy.mock.calls[0][0].url).toBe('http://localhost:5000/api/v1/orders/abc/cancel');
  });

  it('renders an unreached result differently from a response', () => {
    host.proxy.mockReturnValue(of({ outcome: 'unreached', error: 'Connection refused', elapsedMs: 3, correlationId: 'c' }));
    const fixture = render();
    click(fixture, 'Send');

    const response = fixture.nativeElement.querySelector('.response.unreached') as HTMLElement;
    expect(response.textContent).toContain('Connection refused');
  });

  it('shows the problem detail when the host refuses to send', () => {
    host.proxy.mockReturnValue(throwError(() => ({ error: { title: 'Request not sent', detail: 'not one of the configured API surfaces' } })));
    const fixture = render();
    click(fixture, 'Send');

    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('not one of the configured API surfaces');
  });

  it('keeps a history, newest first, and restores a past response when clicked', () => {
    host.proxy.mockReturnValueOnce(of(responded)).mockReturnValueOnce(of({ ...responded, status: 403, body: '', correlationId: 'corr-2' }));
    const fixture = render();
    click(fixture, 'Send');
    click(fixture, 'Send');

    const rows = Array.from(fixture.nativeElement.querySelectorAll('.history li button')) as HTMLButtonElement[];
    expect(rows.map((r) => r.textContent)).toEqual([expect.stringContaining('403'), expect.stringContaining('200')]);

    rows[1].click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.response .correlation')?.textContent).toContain('corr-1');
  });

  it('restoring an entry restores its operation and headers, so Send mints a fresh commandId for it', () => {
    let n = 0;
    TestBed.overrideProvider(UUID, { useValue: () => `uuid-${++n}` });
    const fixture = render();
    const page = fixture.componentInstance;
    click(fixture, 'POST PublishProduct');
    page.headersText.set('Accept-Language: en');
    click(fixture, 'Send');

    click(fixture, 'GET GetProducts');
    page.headersText.set('');
    page.setQueryValue('limit', '5');
    (fixture.nativeElement.querySelector('.history li button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(page.selectedId()).toBe('catalog:PublishProduct');
    expect(page.headersText()).toBe('Accept-Language: en');
    expect(page.queryValues()).toEqual({});
    click(fixture, 'Send');

    const resent = host.proxy.mock.calls[1][0];
    expect(resent.url).toBe('http://localhost:5000/api/v1/catalog/products/');
    expect(resent.headers).toEqual({ 'Accept-Language': 'en' });
    expect(JSON.parse(resent.body).commandId).toBe('uuid-2');
  });

  it('restoring an entry cancels a pending send: a late response is dropped', () => {
    const fixture = render();
    click(fixture, 'Send');
    const subject = new Subject<ProxyResult>();
    host.proxy.mockReturnValue(subject);
    click(fixture, 'Send');

    (fixture.nativeElement.querySelector('.history li button') as HTMLButtonElement).click();
    fixture.detectChanges();
    subject.next({ ...responded, status: 500, correlationId: 'late' });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.response .correlation')?.textContent).toContain('corr-1');
    expect(fixture.nativeElement.querySelectorAll('.history li')).toHaveLength(1);
    expect(fixture.nativeElement.querySelector('button.send')?.disabled).toBe(false);
  });

  it('refuses to send or show a token for a custom identity with no username', () => {
    const fixture = render();
    const page = fixture.componentInstance;
    page.setCustom('  ', 'pw');
    click(fixture, 'Send');

    expect(host.proxy).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('Enter a username for the custom identity.');

    page.error.set(null);
    click(fixture, 'Show token');
    expect(host.token).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('Enter a username for the custom identity.');
  });

  it('notes a body that broke off after the response began', () => {
    host.proxy.mockReturnValue(of({ ...responded, body: '{"items":[', bodyError: 'The response ended prematurely.' }));
    const fixture = render();
    click(fixture, 'Send');

    expect(fixture.nativeElement.querySelector('.response .body-error')?.textContent).toContain('The response ended prematurely.');
  });

  it('shows the token claims for the chosen identity', () => {
    const fixture = render();
    click(fixture, 'Show token');

    expect(host.token).toHaveBeenCalledWith({ username: 'demo', password: null });
    expect(fixture.nativeElement.querySelector('.claims')?.textContent).toContain('catalog:write');
  });

  it('reloads the catalog', () => {
    const fixture = render();
    click(fixture, 'Reload');

    expect(host.reloadOperations).toHaveBeenCalled();
  });

  it('selecting another operation clears a previous error and response', () => {
    host.proxy.mockReturnValueOnce(throwError(() => ({ error: { detail: 'boom' } })));
    const fixture = render();
    click(fixture, 'Send');
    expect(fixture.nativeElement.querySelector('.error')).not.toBeNull();

    host.proxy.mockReturnValue(of(responded));
    click(fixture, 'POST PublishProduct');

    expect(fixture.nativeElement.querySelector('.error')).toBeNull();
    expect(fixture.nativeElement.querySelector('.response')).toBeNull();
  });

  it('selecting another operation cancels a pending send: a late response is dropped', () => {
    const subject = new Subject<ProxyResult>();
    host.proxy.mockReturnValue(subject);
    const fixture = render();
    click(fixture, 'Send');
    click(fixture, 'POST PublishProduct');

    subject.next(responded);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.response')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.history li')).toHaveLength(0);
    expect(fixture.nativeElement.querySelector('button.send')?.disabled).toBe(false);
  });
});
