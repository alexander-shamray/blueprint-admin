import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';

describe('App', () => {
  it('shows the backend directory from the host config', async () => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    TestBed.inject(HttpTestingController).expectOne('/api/config').flush({
      backendDir: 'C:/dev/blueprint-backend', frontendDir: '', composeFile: '', fakePlatform: true,
      urls: { gateway: '', catalog: '', ordering: '', bff: '', keycloak: '', grafana: '', client: '' },
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('C:/dev/blueprint-backend');
    expect(fixture.nativeElement.textContent).toContain('(fake platform)');
  });
});
