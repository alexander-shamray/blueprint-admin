import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { SseClient } from '../../core/host/sse-client';
import { BrokerPage } from './broker-page';
import { brokerHost, rows } from './broker-testing';

describe('BrokerPage', () => {
  it('is the six Broker widgets one under another, which the legacy screen widget renders', () => {
    const host = brokerHost();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => of() } },
      ],
    });
    const fixture = TestBed.createComponent(BrokerPage);
    fixture.detectChanges();

    const element: HTMLElement = fixture.nativeElement;
    expect([...element.querySelectorAll('h2')].map((h) => h.textContent)).toEqual([
      'Queues',
      'BFF order projection',
      'Dead letters',
      'Exchanges',
      'Bindings',
      'Permissions',
    ]);
    expect(rows(fixture, 'queues').length).toBe(6);
    expect(rows(fixture, 'exchanges').length).toBe(2);
    expect(element.querySelector('section.projection')).not.toBeNull();
    // The workbench is on the page, so a parked queue offers to open into it.
    expect(element.querySelectorAll('button.open-dead-letters').length).toBe(2);
  });
});
