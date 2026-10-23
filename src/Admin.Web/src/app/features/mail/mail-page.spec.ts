import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { MailPage } from './mail-page';

describe('MailPage', () => {
  function render(query: Record<string, string>) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
        {
          provide: HostClient,
          useValue: {
            mailMessages: () =>
              of({ reachable: true, error: null, total: 0, messages: [], sinkUrl: 'http://localhost:8025' }),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(MailPage);
    fixture.detectChanges();
    return fixture;
  }

  it('is the Mail widget, which the legacy screen widget renders', () => {
    expect(render({}).nativeElement.querySelector('app-mail-widget')).not.toBeNull();
  });

  it('starts the widget filtered to the correlation id the route carries', async () => {
    const fixture = render({ correlationId: 'an-order' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement.querySelector('input.filter') as HTMLInputElement).value).toBe('an-order');
  });
});
