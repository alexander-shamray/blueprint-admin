import { LOCALE_ID } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { loadLocale } from './app/core/i18n/load-locale';
import { readChosenLocale } from './app/core/i18n/locales';

// Translations load before the app's modules do (AI assistant design §5.5): a `$localize` evaluated when its module
// loads must find them already in place, so the app is imported only once they are.
loadLocale(readChosenLocale())
  .then(async (locale) => {
    document.documentElement.lang = locale;
    const [{ appConfig }, { App }] = await Promise.all([import('./app/app.config'), import('./app/app')]);
    return bootstrapApplication(App, {
      ...appConfig,
      providers: [...appConfig.providers, { provide: LOCALE_ID, useValue: locale }],
    });
  })
  .catch((err) => console.error(err));
