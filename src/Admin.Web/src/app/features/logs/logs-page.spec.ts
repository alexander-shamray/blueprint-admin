import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { SseClient } from '../../core/host/sse-client';
import { LogsPage } from './logs-page';

describe('LogsPage', () => {
  it('is the Logs widget over every service, the legacy screen widget renders', () => {
    const host = { followLogs: vi.fn(() => of({ id: 'logs-1' })), stopFollow: vi.fn(() => of(undefined)) };
    TestBed.configureTestingModule({
      providers: [
        { provide: HostClient, useValue: host },
        { provide: SseClient, useValue: { follow: () => of() } },
      ],
    });
    const fixture = TestBed.createComponent(LogsPage);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('app-logs-widget')).not.toBeNull();
    (fixture.nativeElement.querySelector('button.follow') as HTMLButtonElement).click();
    expect(host.followLogs).toHaveBeenCalledWith([]);
  });
});
