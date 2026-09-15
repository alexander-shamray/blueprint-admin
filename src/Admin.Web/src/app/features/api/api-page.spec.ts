import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ApiCatalogView, ApiOperation, ProxyResult, TokenView } from '../../core/host/host-types';
import { IdentityState } from '../../core/identity/identity-state';
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

  it('history keeps no pasted Authorization header, and a restore leaves it to be entered again', () => {
    const fixture = render();
    const page = fixture.componentInstance;
    page.chooseIdentity('anonymous');
    page.headersText.set('Accept: application/json\nauthorization: Bearer pasted.jwt.token');
    click(fixture, 'Send');

    expect(host.proxy.mock.calls[0][0].headers).toEqual({ Accept: 'application/json', authorization: 'Bearer pasted.jwt.token' });
    expect(JSON.stringify(page.history())).not.toContain('pasted.jwt.token');
    page.headersText.set('');
    (fixture.nativeElement.querySelector('.history li button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(page.headersText()).toBe('Accept: application/json');
  });

  it('history keeps no Cookie sent or Set-Cookie received, while the live response still shows it', () => {
    host.proxy.mockReturnValue(of({ ...responded, headers: { 'Set-Cookie': ['sid=received-secret'], 'Content-Type': ['application/json'] } }));
    const fixture = render();
    const page = fixture.componentInstance;
    page.headersText.set('Cookie: session=sent-secret');
    click(fixture, 'Send');

    expect(page.headerLines()).toContain('Set-Cookie: sid=received-secret');
    const stored = JSON.stringify(page.history());
    expect(stored).not.toContain('sent-secret');
    expect(stored).not.toContain('received-secret');
    expect(stored).toContain('application/json');
  });

  it('announces a response politely and an error as an alert', () => {
    const fixture = render();
    const status = fixture.nativeElement.querySelector('[role="status"]') as HTMLElement;
    expect(status).not.toBeNull();
    click(fixture, 'Send');
    expect(status.textContent).toContain('200');

    host.proxy.mockReturnValue(throwError(() => ({ error: { detail: 'boom' } })));
    click(fixture, 'Send');
    expect(fixture.nativeElement.querySelector('.error')?.getAttribute('role')).toBe('alert');
  });

  it('marks the selected operation for assistive technology', () => {
    const fixture = render();
    click(fixture, 'POST PublishProduct');

    const current = Array.from(fixture.nativeElement.querySelectorAll('button.op[aria-current="true"]')) as HTMLElement[];
    expect(current.map((b) => b.textContent?.trim())).toEqual([expect.stringContaining('PublishProduct')]);
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

  it('restoring an entry restores its url template and parameter values, so editing one changes what is sent', () => {
    const fixture = render();
    const page = fixture.componentInstance;
    click(fixture, 'POST CancelOrder');
    page.setPathValue('id', 'abc');
    click(fixture, 'Send');
    click(fixture, 'GET GetProducts');
    page.setQueryValue('limit', '5');
    click(fixture, 'Send');

    click(fixture, 'GET GetProducts');
    (fixture.nativeElement.querySelectorAll('.history li button')[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(page.url()).toBe('http://localhost:5000/api/v1/orders/{id}/cancel');
    expect(page.pathValues()).toEqual({ id: 'abc' });
    page.setPathValue('id', 'def');
    click(fixture, 'Send');
    expect(host.proxy.mock.calls[2][0].url).toBe('http://localhost:5000/api/v1/orders/def/cancel');

    (fixture.nativeElement.querySelectorAll('.history li button')[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(page.queryValues()).toEqual({ limit: '5' });
    page.setQueryValue('limit', '6');
    click(fixture, 'Send');
    expect(host.proxy.mock.calls[3][0].url).toBe('http://localhost:5000/api/v1/catalog/products/?limit=6');
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

  it('leaving the screen forgets a custom password; returning shows the username and a blank password, as will be sent', () => {
    const first = render();
    first.componentInstance.setCustom('alice', 's3cret');
    first.destroy();

    expect(TestBed.inject(IdentityState).choice()).toEqual({ kind: 'custom', username: 'alice', password: '' });
    const page = render().componentInstance;
    expect(page.customUsername()).toBe('alice');
    expect(page.customPassword()).toBe('');
  });

  it('restoring an entry restores the identity it was sent as', () => {
    const fixture = render();
    const page = fixture.componentInstance;
    page.chooseIdentity('anonymous');
    click(fixture, 'Send');
    page.setCustom('alice', 's3cret');
    click(fixture, 'Send');
    page.chooseIdentity('user:demo');

    const rows = Array.from(fixture.nativeElement.querySelectorAll('.history li button')) as HTMLButtonElement[];
    rows[1].click();
    fixture.detectChanges();
    expect(page.identityValue()).toBe('anonymous');
    click(fixture, 'Send');
    expect(host.proxy.mock.calls[2][0].identity).toBeNull();

    expect(JSON.stringify(page.history())).not.toContain('s3cret');
    (fixture.nativeElement.querySelectorAll('.history li button')[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(page.identity.choice()).toEqual({ kind: 'custom', username: 'alice', password: '' });
    expect(page.customUsername()).toBe('alice');
    expect(page.customPassword()).toBe('');
  });

  it('changing identity drops a token still on its way for the previous one', () => {
    const pending = new Subject<TokenView>();
    host.token.mockReturnValue(pending);
    const fixture = render();
    click(fixture, 'Show token');

    fixture.componentInstance.chooseIdentity('user:browser');
    pending.next({ username: 'demo', accessToken: 'a.b.c', expiresAt: '2026-09-15T08:05:00Z', claims: { permission: ['catalog:write'] } });
    fixture.detectChanges();

    expect(fixture.componentInstance.token()).toBeNull();
    expect(fixture.nativeElement.querySelector('.claims')).toBeNull();
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

  it('shows a repeated response header as one line per value', () => {
    host.proxy.mockReturnValue(of({ ...responded, headers: { 'Set-Cookie': ['a=1; Expires=Wed, 16 Sep 2026 08:00:00 GMT', 'b=2'] } }));
    const fixture = render();
    click(fixture, 'Send');

    expect(fixture.componentInstance.headerLines()).toEqual(['Set-Cookie: a=1; Expires=Wed, 16 Sep 2026 08:00:00 GMT', 'Set-Cookie: b=2']);
  });

  it('a new send clears the previous response, so a refused one is not shown beside it', () => {
    const fixture = render();
    click(fixture, 'Send');
    expect(fixture.nativeElement.querySelector('.response')).not.toBeNull();

    fixture.componentInstance.headersText.set('not a header');
    click(fixture, 'Send');
    expect(fixture.nativeElement.querySelector('.error')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.response')).toBeNull();

    fixture.componentInstance.headersText.set('');
    host.proxy.mockReturnValue(throwError(() => ({ error: { detail: 'not sent' } })));
    click(fixture, 'Send');
    expect(fixture.nativeElement.querySelector('.response')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.history li')).toHaveLength(1);
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
    expect(fixture.nativeElement.querySelector('.access-token')?.textContent).toContain('a.b.c');
    expect(fixture.nativeElement.querySelector('.expires')?.textContent).toContain('2026-09-15T08:05:00Z');
  });

  it('reloads the catalog', () => {
    const fixture = render();
    click(fixture, 'Reload');

    expect(host.reloadOperations).toHaveBeenCalled();
  });

  it('a reload that succeeds after one that failed clears the error', () => {
    host.reloadOperations.mockReturnValueOnce(throwError(() => ({ message: 'down' })));
    const fixture = render();
    click(fixture, 'Reload');
    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('down');

    click(fixture, 'Reload');
    expect(fixture.nativeElement.querySelector('.error')).toBeNull();
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
