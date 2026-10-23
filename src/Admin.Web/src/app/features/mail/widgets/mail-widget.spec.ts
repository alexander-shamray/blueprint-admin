import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { HostClient } from '../../../core/host/host-client';
import { MailListView, MailMessageRead, MailSummary } from '../../../core/host/host-types';
import { WIDGET_CONTEXT } from '../../../workspace/widget-context';
import { MailWidget } from './mail-widget';

const ORDER = '01a13f79-2194-77a5-bbad-eaf2d69eacd6';

function summary(over: Partial<MailSummary>): MailSummary {
  return {
    id: 'm1',
    messageId: '01a13f792ecd799798355445d33ba803.order-confirmed@commerce.test',
    from: { name: 'Commerce', address: 'no-reply@commerce.test' },
    to: [{ name: '', address: 'demo@example.invalid' }],
    subject: 'Your order is confirmed',
    created: '2026-10-15T12:11:11Z',
    snippet: 'Hello',
    read: false,
    correlationId: ORDER,
    ...over,
  };
}

function listed(messages: MailSummary[], over: Partial<MailListView> = {}): MailListView {
  return { reachable: true, error: null, total: messages.length, messages, sinkUrl: 'http://localhost:8025', ...over };
}

function read(over: Partial<MailMessageRead['message']> & object, html = ''): MailMessageRead {
  return {
    reachable: true,
    error: null,
    message: {
      id: 'm1',
      from: { name: 'Commerce', address: 'no-reply@commerce.test' },
      to: [{ name: '', address: 'demo@example.invalid' }],
      cc: [],
      subject: 'Your order is confirmed',
      date: '2026-10-15T12:11:11Z',
      text: 'Order: ' + ORDER,
      html,
      correlationId: ORDER,
      ...over,
    },
  };
}

describe('MailWidget', () => {
  function render(
    list: Observable<MailListView>,
    opened: MailMessageRead = read({}),
    config: Record<string, unknown> = {},
    extraProviders: unknown[] = [],
  ) {
    const mailMessage = vi.fn().mockReturnValue(of(opened));
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: HostClient, useValue: { mailMessages: () => list, mailMessage } }, ...(extraProviders as [])],
    });
    const fixture = TestBed.createComponent(MailWidget);
    fixture.componentRef.setInput('config', config);
    fixture.detectChanges();
    return { fixture, mailMessage };
  }

  function el(fixture: { nativeElement: HTMLElement }): HTMLElement {
    return fixture.nativeElement;
  }

  function rows(fixture: { nativeElement: HTMLElement }): string[] {
    return (Array.from(el(fixture).querySelectorAll('table.messages tbody tr')) as HTMLElement[]).map((r) => r.textContent?.replace(/\s+/g, ' ').trim() ?? '');
  }

  it('lists each message with its correlation id as a link into its trace, and links the sink', () => {
    const { fixture } = render(of(listed([summary({ id: 'm2', subject: 'Delivered' }), summary({})])));

    expect(rows(fixture)).toHaveLength(2);
    expect(rows(fixture)[0]).toContain('Delivered');
    const link = el(fixture).querySelector('td.correlation a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe(`/trace/${ORDER}`);
    expect((el(fixture).querySelector('a.sink-link') as HTMLAnchorElement).href).toBe('http://localhost:8025/');
    expect(el(fixture).querySelector('.no-correlation')).toBeNull();
  });

  it('says when no message carries a correlation id, and links none', () => {
    const { fixture } = render(of(listed([summary({ correlationId: null })])));

    expect(el(fixture).querySelector('.no-correlation')?.textContent).toContain('X-Correlation-Id');
    expect(el(fixture).querySelector('td.correlation a')).toBeNull();
    expect(el(fixture).querySelector('td.correlation')?.textContent).toContain('none');
  });

  it('narrows the list to the correlation id its config names', async () => {
    const { fixture } = render(of(listed([summary({ id: 'a' }), summary({ id: 'b', correlationId: 'other-order' })])), read({}), {
      correlationId: 'other-order',
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(rows(fixture)).toHaveLength(1);
    expect(el(fixture).querySelector('.count')?.textContent).toContain('1 of the 2 listed');
    expect((el(fixture).querySelector('input.filter') as HTMLInputElement).value).toBe('other-order');
  });

  it('shows what the operator types over the configured filter', async () => {
    const { fixture } = render(of(listed([summary({ id: 'a' }), summary({ id: 'b', correlationId: 'other-order' })])), read({}), {
      correlationId: 'other-order',
    });
    const input = el(fixture).querySelector('input.filter') as HTMLInputElement;

    input.value = '';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(rows(fixture)).toHaveLength(2);
  });

  it('says the list is the newest of more than it shows', () => {
    const { fixture } = render(of(listed([summary({})], { total: 120 })));

    expect(el(fixture).querySelector('.count')?.textContent).toContain('the newest of 120');
  });

  it('says why the sink did not answer', () => {
    const { fixture } = render(of(listed([], { reachable: false, error: 'connection refused' })));

    expect(el(fixture).querySelector('.error')?.textContent).toContain('The mail sink did not answer: connection refused');
    expect(el(fixture).querySelector('table.messages')).toBeNull();
  });

  it('says the host did not answer, and not Loading', () => {
    const { fixture } = render(throwError(() => new Error('down')));

    expect(el(fixture).querySelector('.error')?.textContent).toContain('The host did not answer.');
    expect(el(fixture).textContent).not.toContain('Loading');
  });

  it('opens a plain-text message as text', () => {
    const { fixture, mailMessage } = render(of(listed([summary({})])));

    (el(fixture).querySelector('button.open') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(mailMessage).toHaveBeenCalledWith('m1');
    expect(el(fixture).querySelector('pre.body-text')?.textContent).toContain(ORDER);
    expect(el(fixture).querySelector('iframe')).toBeNull();
    expect(el(fixture).querySelector('section.message .facts a')?.getAttribute('href')).toBe(`/trace/${ORDER}`);
  });

  it('renders an HTML part in a frame sandboxed with no scripts', () => {
    const { fixture } = render(of(listed([summary({})])), read({}, '<style>p{color:red}</style><p>Hello</p>'));

    (el(fixture).querySelector('button.open') as HTMLButtonElement).click();
    fixture.detectChanges();

    const frame = el(fixture).querySelector('iframe.body-html') as HTMLIFrameElement;
    expect(frame.getAttribute('sandbox')).toBe('');
    expect(frame.srcdoc).toContain('<style>p{color:red}</style>');
    expect(el(fixture).querySelector('pre.body-text')).toBeNull();
  });

  it("shows the sink's own answer when a message is gone", () => {
    const { fixture } = render(of(listed([summary({})])), { reachable: true, error: 'The mail sink answered 404 for message m1.', message: null });

    (el(fixture).querySelector('button.open') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(el(fixture).querySelector('.error')?.textContent).toContain('answered 404');
    expect(el(fixture).querySelector('section.message')).toBeNull();
  });

  it('is re-created with the message that was open and the filter that was typed (session state, §4.2)', async () => {
    const context = { provide: WIDGET_CONTEXT, useValue: { instanceId: 'w1', pageId: 'p1' } };
    const { fixture, mailMessage } = render(of(listed([summary({})])), read({}), {}, [context]);
    (el(fixture).querySelector('button.open') as HTMLButtonElement).click();
    fixture.detectChanges();
    const input = el(fixture).querySelector('input.filter') as HTMLInputElement;
    input.value = ORDER;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    fixture.destroy();

    const again = TestBed.createComponent(MailWidget);
    again.componentRef.setInput('config', {});
    again.detectChanges();
    await again.whenStable();
    again.detectChanges();

    expect(el(again).querySelector('section.message h2')?.textContent).toContain('Your order is confirmed');
    expect((el(again).querySelector('input.filter') as HTMLInputElement).value).toBe(ORDER);
    // Only the id was kept: the message is read again, so a sink cleared meanwhile is not shown from memory.
    expect(mailMessage).toHaveBeenCalledTimes(2);
  });

  it('starts at the correlation id an old link carries in its route, over the configured one', async () => {
    const route = { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ correlationId: 'from-link' }) } } };
    const { fixture } = render(
      of(listed([summary({ id: 'a', correlationId: 'from-link' }), summary({ id: 'b', correlationId: 'configured' })])),
      read({}),
      { correlationId: 'configured' },
      [route],
    );
    await fixture.whenStable();
    fixture.detectChanges();

    expect(rows(fixture)).toHaveLength(1);
    expect((el(fixture).querySelector('input.filter') as HTMLInputElement).value).toBe('from-link');
  });

  it('takes away what was typed when another configured filter is chosen', async () => {
    const { fixture } = render(of(listed([summary({ id: 'a' }), summary({ id: 'b', correlationId: 'other-order' })])));
    const input = el(fixture).querySelector('input.filter') as HTMLInputElement;
    input.value = 'other-order';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(rows(fixture)).toHaveLength(1);

    fixture.componentRef.setInput('config', { correlationId: ORDER });
    fixture.detectChanges();

    expect(rows(fixture)).toHaveLength(1);
    expect(el(fixture).querySelector('td.correlation a')?.getAttribute('href')).toBe(`/trace/${ORDER}`);
  });
});
