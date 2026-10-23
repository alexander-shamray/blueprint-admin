import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ErasurePage } from './erasure-page';

describe('ErasurePage', () => {
  it('is the Erasure widget, which the legacy screen widget renders', () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: HostClient,
          useValue: {
            config: () => of({ urls: { gateway: 'http://gw.test/' } }),
            erasureHolders: () =>
              of({
                reachable: true,
                error: null,
                holders: [{ name: 'ordering', queue: 'q', pending: 0 }],
                completionsPending: 0,
                runbookUrl: 'x',
              }),
            proxy: vi.fn(),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(ErasurePage);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('app-erasure-widget')).not.toBeNull();
    expect(fixture.nativeElement.querySelectorAll('table.holders tbody tr').length).toBe(1);
  });
});
