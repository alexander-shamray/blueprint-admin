import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { MapPage } from './map-page';
import { view } from './map-testing';

describe('MapPage', () => {
  it('is the Platform map widget, which the legacy screen widget renders', () => {
    const host = { platformMap: vi.fn(() => of(view)) };
    TestBed.configureTestingModule({ providers: [{ provide: HostClient, useValue: host }] });
    const fixture = TestBed.createComponent(MapPage);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('app-map-widget')).not.toBeNull();
    expect(fixture.nativeElement.querySelectorAll('section.service').length).toBe(3);
  });
});
