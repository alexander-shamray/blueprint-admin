import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { clearTranslations, loadTranslations } from '@angular/localize';
import { App } from './app';
import { AssistantService } from './assistant/assistant-service';
import { SCREENS } from './screens';

describe('App', () => {
  it('shows the backend directory from the host config', async () => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    TestBed.inject(HttpTestingController)
      .expectOne('/api/config')
      .flush({
        backendDir: 'C:/dev/blueprint-backend',
        frontendDir: '',
        composeFile: '',
        fakePlatform: true,
        urls: {
          gateway: '',
          catalog: '',
          ordering: '',
          bff: '',
          inventory: '',
          payments: '',
          paymentSimulator: '',
          carrierSimulator: '',
          keycloak: '',
          grafana: '',
          client: '',
        },
      });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('C:/dev/blueprint-backend');
    expect(fixture.nativeElement.textContent).toContain('(fake platform)');
  });

  it('still renders the shell when the initial config request fails', async () => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    TestBed.inject(HttpTestingController)
      .expectOne('/api/config')
      .flush('offline', { status: 0, statusText: 'Unknown Error' });
    await fixture.whenStable();
    fixture.detectChanges();

    const nav = fixture.nativeElement.querySelectorAll('nav a');
    expect(nav.length).toBe(SCREENS.length);
    expect(fixture.nativeElement.querySelector('router-outlet')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.hint')).toBeFalsy();
  });

  it('offers the Assistant widget type in the picker only while the assistant is on (AI assistant design §9.1)', () => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(App);
    const assistant = TestBed.inject(AssistantService);
    const types = (): string[] => fixture.componentInstance.widgetTypes().map((w) => w.type);

    assistant.status.set({ enabled: false, provider: null, model: null, reason: 'off' });
    expect(types()).not.toContain('assistant');
    expect(types()).toContain('note');

    assistant.status.set({ enabled: true, provider: 'fake', model: 'scripted', reason: null });
    expect(types()).toContain('assistant');
  });

  it('reads in the language whose translations loaded before it rendered', () => {
    // The id `ng extract-i18n` gives the side navigation's label (src/locale/messages.ru-RU.json).
    loadTranslations({ '3817498941817715969': 'Страницы' });
    try {
      TestBed.configureTestingModule({
        imports: [App],
        providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
      });
      const fixture = TestBed.createComponent(App);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('nav').getAttribute('aria-label')).toBe('Страницы');
    } finally {
      clearTranslations();
    }
  });

  it('holds the language switch while a layout change waits to be saved, since the switch reloads', () => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const app = TestBed.createComponent(App).componentInstance;

    expect(app.canSwitchLanguage()).toBe(true);
    for (const kind of ['pending', 'saving'] as const) {
      app.workspace.saveState.set({ kind });
      expect(app.canSwitchLanguage()).toBe(false);
    }
    app.workspace.saveState.set({ kind: 'failed', message: 'offline' });
    expect(app.canSwitchLanguage()).toBe(false);
    app.workspace.saveState.set({ kind: 'unavailable', message: 'offline' });
    expect(app.canSwitchLanguage()).toBe(true);
  });
});
